using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;
namespace APIShared.GateRouting.Native
{
    public sealed partial class GateRoutingNativeHooks
    {
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PathBuilderDelegate(IntPtr manager, int playerId, int profile);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void AttackApproachDelegate(IntPtr manager, int tribeId,
            int targetContext, uint x, uint y, int resultCount, int region, int movementClass);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void BuildingApproachDelegate(IntPtr manager, int tribeId,
            int buildingId, int resultCount, int region, int movementClass);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void BuildingConsumerDelegate(IntPtr tribeManager, int tribeId, int variant);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void CursorMoveDelegate(IntPtr unitManager, int tribeId,
            int targetX, int targetY, int targetContext, int actionFlags);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void CandidateSearchDelegate(IntPtr manager, int startTileId,
            int resultCount, int targetPcl, int playerId, int mode, int candidateClass);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int DirectTileSearchDelegate(IntPtr manager, int x, int y,
            int argument4, int argument5, int argument6);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PclReachabilityDelegate(IntPtr manager, int player,
            int targetPcl, int sourcePcl, int mode);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int CursorPclDecisionDelegate(IntPtr manager, int player,
            int targetPcl, int sourcePcl, int mode, int unitId);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void AiTacticalTargetDelegate(IntPtr tribeManager, int tribeId);
    }
}
