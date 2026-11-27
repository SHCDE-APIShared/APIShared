namespace APIShared
{
    internal sealed partial class MissionLifecycleService
    {
        internal NativeWorldReadReader CreateNativeWorldReadReader(object engineMonitor) =>
            new NativeWorldReadReader(engineMonitor, ReadNativeClockForProtectedRead, ReadReadyForProtectedRead);

        private MissionLifecycleState.NativeClockState ReadNativeClockForProtectedRead() => state.NativeClock;
        private MissionContext ReadReadyForProtectedRead() => state.Current;
    }
}
