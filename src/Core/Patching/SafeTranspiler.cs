using System;
using System.Collections.Generic;
using System.Linq;
using APIShared.Internal;
using BepInEx.Logging;
using HarmonyLib;

namespace APIShared.Patching
{
    /// <summary>Runs a Harmony transpiler so that its failure never breaks other patches of the method.</summary>
    /// <remarks>
    /// Harmony rebuilds a method from its original IL whenever any patch of that method changes and runs
    /// every transpiler again. A transpiler that throws makes that patch operation fail and drops every
    /// patch of the method, also those of other mods. Call this from the transpiler instead: the edit
    /// runs on copies of the instructions and is fully evaluated here, and on any exception the
    /// unchanged original instructions are returned and the failure is reported as a mod error.
    /// <para>Validate the original IL before patching (for example with
    /// <c>PatchProcessor.GetOriginalInstructions</c>) so that an unsupported game version fails the
    /// installation instead of silently leaving the method unchanged.</para>
    /// </remarks>
    public static class SafeTranspiler
    {
        /// <summary>Applies <paramref name="transform"/>, or returns the original instructions if it throws.</summary>
        /// <param name="ownerGuid">The patching mod's GUID, used in the failure report.</param>
        /// <param name="id">A stable patch name, used in the failure report.</param>
        /// <param name="instructions">The transpiler input.</param>
        /// <param name="transform">The edit; it may change the given instruction objects and may throw.</param>
        /// <param name="log">Receives failure reports; may be null.</param>
        /// <param name="outcome">Optional; receives true when the edit was used and false when it was dropped.</param>
        /// <returns>The edited or the unchanged instructions.</returns>
        public static IEnumerable<CodeInstruction> Run(string ownerGuid, string id, IEnumerable<CodeInstruction> instructions,
            Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>> transform, ManualLogSource log, Action<bool> outcome = null)
        {
            if (instructions == null) throw new ArgumentNullException(nameof(instructions));
            if (transform == null) throw new ArgumentNullException(nameof(transform));
            List<CodeInstruction> original = instructions.ToList();
            List<CodeInstruction> result;
            try
            {
                // Copies keep the original objects untouched for the fallback. The copy constructor keeps
                // labels and exception blocks; CodeInstruction.Clone() would drop them and break every branch.
                result = transform(CopyInstructions(original)).ToList();
            }
            catch (Exception error)
            {
                try { RuntimeHealth.Failure(log ?? RuntimeHealth.ProcessLogger, "Patching/" + ownerGuid + "/" + id, true,
                    "Transpiler " + id + " (" + ownerGuid + ") left the method unchanged: " + error); }
                catch { }
                Notify(outcome, false, ownerGuid, id, log);
                return original;
            }
            Notify(outcome, true, ownerGuid, id, log);
            return result;
        }

        private static List<CodeInstruction> CopyInstructions(List<CodeInstruction> original)
        {
            // Harmony's copy constructor shares switch arrays and mutable exception blocks.
            // Preserve aliases within the edited graph, while detaching it from the fallback.
            var metadata = new Dictionary<object, object>(MetadataIdentity.Instance);
            var copies = new List<CodeInstruction>(original.Count);
            foreach (var instruction in original)
            {
                var copy = new CodeInstruction(instruction);
                if (instruction.operand is System.Reflection.Emit.Label[] targets)
                {
                    if (!metadata.TryGetValue(targets, out var cloned)) metadata.Add(targets, cloned = targets.Clone());
                    copy.operand = cloned;
                }
                for (int i = 0; i < copy.blocks.Count; i++)
                {
                    var block = instruction.blocks[i];
                    if (block == null) continue;
                    if (!metadata.TryGetValue(block, out var cloned))
                        metadata.Add(block, cloned = new ExceptionBlock(block.blockType, block.catchType) { catchType = block.catchType });
                    copy.blocks[i] = (ExceptionBlock)cloned;
                }
                copies.Add(copy);
            }
            return copies;
        }

        private sealed class MetadataIdentity : IEqualityComparer<object>
        {
            internal static readonly MetadataIdentity Instance = new MetadataIdentity();
            public new bool Equals(object left, object right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }

        private static void Notify(Action<bool> outcome, bool applied, string ownerGuid, string id, ManualLogSource log)
        {
            if (outcome == null) return;
            try { outcome(applied); }
            catch (Exception error)
            {
                try { RuntimeHealth.Failure(log ?? RuntimeHealth.ProcessLogger, "Patching/" + ownerGuid + "/" + id, true, "Transpiler outcome handler failed: " + error.Message); }
                catch { }
            }
        }
    }
}
