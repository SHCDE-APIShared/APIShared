using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;

namespace APIShared.ProtonWorkshopFix
{
    internal sealed class ExtenderContract
    {
        internal const string ManagerName = "SHCDESE.API.Components.Archive.MapModManager";
        internal MethodInfo CounterMethod, UnixMethod;
        internal MethodInfo[] Getters;
        internal int AttemptLimit;
        internal string ManagerTypeName;

        internal static ExtenderContract Validate(Assembly assembly)
        {
            if (assembly == null || assembly.GetName().Name != "SHCDESE")
                throw new InvalidOperationException("Expected the loaded SHCDESE assembly.");
            return ValidateType(assembly.GetType(ManagerName, true));
        }

        // This overload also permits owned metadata fixtures; runtime always enters through Validate(Assembly).
        internal static ExtenderContract ValidateType(Type type)
        {
            var contract = new ExtenderContract();
            contract.ManagerTypeName = type.FullName.Replace('+', '/');
            var flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            contract.CounterMethod = Method(type, "TryUpdateModsFromRemote", typeof(void));
            Method(type, "ReadDeployAttempts", typeof(int));
            Method(type, "WriteDeployAttempts", typeof(void), typeof(int));
            var launcher = Method(type, "LaunchUpdaterAndExit", typeof(void));
            var cap = type.GetField("MaxDeployAttempts", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (cap == null || !cap.IsLiteral || cap.FieldType != typeof(int) || (int)cap.GetRawConstantValue() <= 0)
                throw new InvalidOperationException("Missing positive constant MaxDeployAttempts.");
            contract.AttemptLimit = (int)cap.GetRawConstantValue();
            foreach (string name in new[] { "_stagingDirectory", "_seDirectory", "_deleteManifestPath", "_deployAttemptsPath" })
            {
                var field = type.GetField(name, flags);
                if (field == null || field.FieldType != typeof(string) || field.IsStatic)
                    throw new InvalidOperationException("Missing Extender path field " + name);
            }
            var unix = type.GetMethods(flags).SingleOrDefault(m => m.Name == "LaunchUpdaterAndExit_Unix");
            if (unix == null || unix.IsStatic || unix.IsPublic || unix.ContainsGenericParameters || unix.ReturnType != typeof(bool) ||
                unix.GetParameters().Length != 1 || !unix.GetParameters()[0].ParameterType.IsByRef || unix.GetMethodBody() == null)
                throw new InvalidOperationException("Incompatible Linux launcher signature.");
            Type context = unix.GetParameters()[0].ParameterType.GetElementType();
            if (!context.IsValueType || context.DeclaringType != type || context.Name != "UpdaterContext")
                throw new InvalidOperationException("Incompatible private updater context.");
            string[] names = { "StagingDirectory", "TargetDirectory", "DeleteManifest", "GameExe", "GamePid" };
            contract.Getters = names.Select((name, index) => {
                var property = context.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                var getter = property?.GetGetMethod();
                if (property == null || property.GetIndexParameters().Length != 0 || getter == null || getter.IsStatic ||
                    getter.ReturnType != (index == names.Length - 1 ? typeof(int) : typeof(string)))
                    throw new InvalidOperationException("Missing updater context getter " + name);
                return getter;
            }).ToArray();
            contract.UnixMethod = unix;
            using (var definition = new DynamicMethodDefinition(contract.CounterMethod))
            using (var contextIl = new ILContext(definition.Definition))
                ExtenderIl.ValidateCounter(contextIl, contract.AttemptLimit, contract.ManagerTypeName);
            using (var definition = new DynamicMethodDefinition(launcher))
                ValidateParentLauncher(definition.Definition, contract.ManagerTypeName);
            return contract;
        }

        private static MethodInfo Method(Type type, string name, Type result, params Type[] parameters)
        {
            var method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, parameters, null);
            if (method == null || method.IsStatic || method.IsPublic || method.ContainsGenericParameters || method.ReturnType != result || method.GetMethodBody() == null)
                throw new InvalidOperationException("Incompatible Extender method: " + name);
            return method;
        }

        internal static void ValidateParentLauncher(MethodDefinition method, string managerTypeName)
        {
            var instructions = method.Body.Instructions;
            // A leave executes enclosing finally/fault/filter control flow. The audited
            // parent has catch-only handlers; linear rejection-path inspection cannot
            // establish success-only shutdown for those other handler kinds.
            if (method.Body.ExceptionHandlers.Any(h => h.HandlerType != ExceptionHandlerType.Catch))
                throw new InvalidOperationException("Unaudited parent launcher exception handler kind.");
            var unix = instructions.SingleOrDefault(i => ExtenderIl.Calls(i, managerTypeName, "LaunchUpdaterAndExit_Unix"));
            var windows = instructions.SingleOrDefault(i => ExtenderIl.Calls(i, managerTypeName, "LaunchUpdaterAndExit_Win32"));
            var kills = instructions.Where(i => ExtenderIl.Calls(i, "System.Diagnostics.Process", "Kill")).ToArray();
            var guard = unix?.Next;
            if (unix == null || windows == null || kills.Length != 1 || guard == null ||
                (guard.OpCode != OpCodes.Brtrue && guard.OpCode != OpCodes.Brtrue_S) || !(guard.Operand is Instruction success))
                throw new InvalidOperationException("Missing success-only game-exit guard.");
            int successIndex = instructions.IndexOf(success), killIndex = instructions.IndexOf(kills[0]);
            foreach (var handler in method.Body.ExceptionHandlers)
            {
                int handlerStart = instructions.IndexOf(handler.HandlerStart);
                int handlerEnd = handler.HandlerEnd == null ? instructions.Count : instructions.IndexOf(handler.HandlerEnd);
                if (handlerStart < 0 || handlerEnd <= handlerStart ||
                    (killIndex >= handlerStart && killIndex < handlerEnd))
                    throw new InvalidOperationException("Parent launcher shutdown may not execute inside an exception handler.");
            }
            if (successIndex <= instructions.IndexOf(guard) || killIndex <= successIndex)
                throw new InvalidOperationException("Game-exit order changed.");
            bool terminates = false;
            for (Instruction i = guard.Next; i != success; i = i?.Next)
            {
                if (i == null || ExtenderIl.Calls(i, "System.Diagnostics.Process", "Kill"))
                    throw new InvalidOperationException("Failed updater can reach game exit.");
                if (i.OpCode == OpCodes.Ret || i.OpCode == OpCodes.Leave || i.OpCode == OpCodes.Leave_S)
                {
                    if (i.Operand is Instruction target && instructions.IndexOf(target) <= killIndex)
                        throw new InvalidOperationException("Failed updater branches into the success path.");
                    terminates = true; break;
                }
                if (i.OpCode.FlowControl == FlowControl.Branch || i.OpCode.FlowControl == FlowControl.Cond_Branch)
                    throw new InvalidOperationException("Unknown failed-updater control flow.");
            }
            if (!terminates) throw new InvalidOperationException("Failed updater has no terminal path.");
        }

        internal Delegate PrepareCounter(bool prepareJit)
        {
            using (var definition = new DynamicMethodDefinition(CounterMethod))
            {
                using (var il = new ILContext(definition.Definition)) ExtenderIl.ModifyCounter(il, AttemptLimit, ManagerTypeName);
                var compiled = definition.Generate().CreateDelegate(typeof(Action<>).MakeGenericType(CounterMethod.DeclaringType));
                if (prepareJit) RuntimeHelpers.PrepareDelegate(compiled);
                return compiled;
            }
        }

        internal Delegate PrepareUnix(bool prepareJit)
        {
            using (var definition = new DynamicMethodDefinition(UnixMethod))
            {
                using (var il = new ILContext(definition.Definition)) ExtenderIl.ModifyUnix(il, this);
                var delegateType = Expression.GetDelegateType(UnixMethod.DeclaringType, UnixMethod.GetParameters()[0].ParameterType, typeof(bool));
                var compiled = definition.Generate().CreateDelegate(delegateType);
                if (prepareJit) RuntimeHelpers.PrepareDelegate(compiled);
                return compiled;
            }
        }
    }
}
