using System;

namespace APIShared.Savegames
{
    /// <summary>Terminal outcome of one dialog request; a rejected confirmation keeps the request open.</summary>
    public enum SaveLoadDialogOutcome
    {
        /// <summary>The original confirmation callback returned; mission startup may still be pending.</summary>
        Confirmed,
        /// <summary>The user cancelled the request.</summary>
        Cancelled,
        /// <summary>A newer request replaced this request.</summary>
        Replaced,
        /// <summary>Opening or executing the original callback failed.</summary>
        Failed
    }

    /// <summary>Owner-private state and selection for one load/save requester registration.</summary>
    /// <remarks>Callbacks run synchronously on the Unity thread. Each registration receives a separate context.
    /// Do not retain the game view or assume confirmation means a successfully started mission.
    /// Exceptions from the original game action propagate after cleanup; contribution gate/preparation errors reject the attempt.</remarks>
    public sealed class SaveLoadDialogContext
    {
        internal SaveLoadDialogContext(long generation, Enums.RequesterTypes type)
        { Generation = generation; RequesterType = type; }
        /// <summary>Process-local identity; a replaced request cannot confirm the new request.</summary>
        public long Generation { get; }
        /// <summary>Original game requester category.</summary>
        public Enums.RequesterTypes RequesterType { get; }
        /// <summary>Confirmed filename, available during validation and preparation.</summary>
        public string FileName { get; internal set; }
        /// <summary>Selected game header; editor saves may have no header.</summary>
        public FileHeader Header { get; internal set; }
        /// <summary>State private to this registration for this request.</summary>
        public object State { get; set; }
    }

    /// <summary>Contributions to the common requester. Delegates are copied when registered.</summary>
    public sealed class SaveLoadDialogCallbacks
    {
        /// <summary>Optional initialization after the original dialog opened. Failure blocks this contribution's confirmation.</summary>
        public Action<SaveLoadDialogContext> Opened { get; set; }
        /// <summary>Required gate when supplied. False or an exception rejects confirmation before any preparation.</summary>
        public Func<SaveLoadDialogContext, bool> Validate { get; set; }
        /// <summary>Preparation after all gates passed. Exceptions reject confirmation and run attempt cleanup.</summary>
        public Action<SaveLoadDialogContext> Prepare { get; set; }
        /// <summary>Runs in reverse order after each prepared attempt, including a partially throwing preparation.
        /// This releases temporary write decisions, not a successfully transferred mission.</summary>
        public Action<SaveLoadDialogContext> AttemptFinished { get; set; }
        /// <summary>Optional observation exactly once when the request terminates, after attempt cleanup;
        /// exceptions are isolated. Requesters opened synchronously by callbacks are deferred until the
        /// outer requester operation finishes, including the original game's bookkeeping. Replacement
        /// immediately invalidates an old request that has not yet invoked its original action.</summary>
        public Action<SaveLoadDialogContext, SaveLoadDialogOutcome> Closed { get; set; }
    }
}
