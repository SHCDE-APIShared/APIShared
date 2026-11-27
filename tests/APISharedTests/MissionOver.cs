using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using APIShared.Events;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    public partial class RuntimeTests
    {
        [TestMethod]
        [TestCategory("Presentation")]
        public void MissionOverDispatchPreservesOriginalAndIsolatesObservers()
        {
            var events = new InterceptionEvent<MissionOverPreEventArgs, MissionOverPostEventArgs>();
            var steps = new List<string>();
            MissionOverPostEventArgs completion = null;
            events.TryRegister("b", "error", args => { args.Result = 99; args.SkipOriginalFunction = true; throw new Exception("fixture"); },
                null, out _, 0);
            events.TryRegister("a", "first", args => { steps.Add("pre"); args.Result = 2; args.State = "private"; },
                args => { steps.Add("post"); completion = args; }, out _, 0,
                (args, state) => { Assert(args.Result == 2 && (string)state == "private", "accepted sees frozen final inputs and private state"); steps.Add("accepted"); });
            var input = new MissionOverPreEventArgs(MissionOverOperation.State, null, null, 1, 7, 8);
            MissionOverEvents.Execute(events, input, args => { Assert(args.Result == 2 && args.Screen == 7 && args.Date == 8, "only presentation result changes"); steps.Add("original"); });
            Assert(string.Join(",", steps) == "pre,accepted,original,post", "ordered callbacks preserve the original exactly once");
            Assert(events.CallbackFailures == 1 && completion.OriginalCompleted && completion.OriginalResult == 1 &&
                completion.Result == 2 && (string)completion.State == "private", "failed Pre rolls back without losing another observer's inputs");
            bool frozen = false; try { input.Result = 4; } catch (InvalidOperationException) { frozen = true; }
            Assert(frozen, "retained Pre cannot mutate after notification");
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void MissionOverPreviewVetoSuppressesLobbyDeferralInEitherOrder()
        {
            foreach (bool reverse in new[] { false, true })
            {
                var events = new InterceptionEvent<MissionOverPreEventArgs, MissionOverPostEventArgs>();
                int originals = 0, deferred = 0, closed = 0;
                events.TryRegister("lobby", "exit", args => args.DeferOriginal(_ => deferred++, () => true), null, out _, reverse ? -1 : 1);
                events.TryRegister("preview", "exit", args => { args.State = 1; args.SkipOriginalFunction = true; },
                    args => { if (args.WasSkipped && (int)args.State == 1) closed++; }, out _);
                MissionOverEvents.Execute(events, ButtonInput(), _ => originals++);
                Assert(originals == 0 && deferred == 0 && closed == 1, "preview exit cannot reach lobby or Vanilla in either order");
            }
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void MissionOverDeferredOriginalChecksContextAndRunsAtMostOnce()
        {
            var events = new InterceptionEvent<MissionOverPreEventArgs, MissionOverPostEventArgs>();
            MissionOverContinuation pending = null;
            MissionOverPostEventArgs completion = null;
            int generation = 1, originals = 0;
            events.TryRegister("lobby", "wait", args => { int captured = generation;
                args.DeferOriginal(token => pending = token, () => generation == captured); }, args => completion = args, out _);
            MissionOverEvents.Execute(events, ButtonInput(), _ => originals++);
            Assert(completion.WasDeferred && !completion.OriginalCompleted && originals == 0, "dispatch completion does not claim delayed execution");
            Assert(!Task.Run(() => pending.TryInvoke()).GetAwaiter().GetResult(), "wrong thread cannot execute the UI original");
            Assert(pending.TryInvoke() && !pending.TryInvoke() && originals == 1, "original executes once on its thread");
            MissionOverEvents.Execute(events, ButtonInput(), _ => originals++);
            generation++;
            Assert(!pending.TryInvoke() && originals == 1, "stale session/view continuation is consumed without executing");
            MissionOverEvents.Execute(events, ButtonInput(), _ => throw new InvalidOperationException("original"));
            bool threw = false; try { pending.TryInvoke(); } catch (InvalidOperationException) { threw = true; }
            Assert(threw && !pending.TryInvoke(), "a throwing original is never retried");
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void MissionOverPostReceivesOriginalExceptionAndAuxiliaryTextData()
        {
            var events = new InterceptionEvent<MissionOverPreEventArgs, MissionOverPostEventArgs>();
            MissionOverPostEventArgs completion = null;
            events.TryRegister("observer", "post", null, args => completion = args, out _);
            var error = new InvalidOperationException("original");
            try { MissionOverEvents.Execute(events, new MissionOverPreEventArgs(MissionOverOperation.Text, null, null, 1, 2, 3, 4, 5), _ => throw error); }
            catch (InvalidOperationException caught) { Assert(ReferenceEquals(caught, error), "original exception preserved"); }
            Assert(completion != null && ReferenceEquals(completion.CompletionException, error) && !completion.OriginalCompleted &&
                completion.Screen == 2 && completion.Date == 3 && completion.Data4 == 4 && completion.Data5 == 5, "post retains all untouched OST inputs on failure");
        }
        private static MissionOverPreEventArgs ButtonInput() => new MissionOverPreEventArgs(MissionOverOperation.Button, null, "Exit", 0, 0, 0);

        [TestMethod]
        [TestCategory("Presentation")]
        public void MissionOverDeferralFailureCannotStrandOrRepeatTheOriginal()
        {
            foreach (bool callFirst in new[] { false, true })
            {
                var events = new InterceptionEvent<MissionOverPreEventArgs, MissionOverPostEventArgs>();
                MissionOverContinuation retained = null;
                int originals = 0;
                events.TryRegister("broken", "defer", args => args.DeferOriginal(token => {
                    retained = token;
                    if (callFirst) token.TryInvoke();
                    throw new InvalidOperationException("consumer callback");
                }, () => true), null, out _);
                MissionOverEvents.Execute(events, ButtonInput(), _ => originals++);
                Assert(originals == 1 && !retained.TryInvoke(), "deferral error falls back once, never repeats an original");
            }
            var guarded = new MissionOverContinuation(() => throw new Exception("must not run"), () => throw new Exception("invalid guard"));
            Assert(!guarded.TryInvoke() && !guarded.TryInvoke(), "a failing guard fails closed and is consumed");
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void MissionOverInstalledBoundariesAndDormantPublishersRetainTheirContracts()
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Assert(typeof(CrusaderDE.HUD_MissionOver).GetMethod("ButtonClicked", flags, null, new[] { typeof(string) }, null)?.ReturnType == typeof(void), "installed button boundary");
            Assert(typeof(GameData.Scenarios).GetMethod("setGameOverState", flags, null, new[] { typeof(int), typeof(int), typeof(int) }, null)?.ReturnType == typeof(void), "installed state boundary");
            Assert(typeof(OnScreenText).GetMethod("addOSTEntry", flags, null, new[] { typeof(Enums.eOnScreenText), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) }, null)?.ReturnType == typeof(void), "installed text boundary");
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(MissionOverEvents).Assembly.Location))
            {
                var type = assembly.MainModule.GetType(typeof(MissionOverEvents).FullName);
                foreach (string name in new[] { "Button", "State", "Text" })
                    Assert(!type.Methods.Single(method => method.Name == name).Body.Instructions.Any(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Newobj), "dormant/unrelated " + name + " publisher has no hoisted allocation");
            }
        }
    }
}
