using System;

namespace APIShared.Presentation
{
    /// <summary>Logical demand for one process-lived Unity presentation callback. Thread-safe; no hook disposal.</summary>
    public interface IUiFrameWorkRegistration
    {
        /// <summary>Requests one before-render call. Repeated requests coalesce; requests during a callback survive for a later frame.</summary>
        void Request();
        /// <summary>Requests a refresh on the first rendered frame at or after this nonnegative monotonic delay, including while paused. The earliest pending request wins; zero is immediate. Invalid or overflowing delays throw before changing demand. This is refresh demand, not an action timer.</summary>
        void RequestAfter(TimeSpan delay);
        /// <summary>Cancels uncaptured immediate and delayed demand, leaving continuous demand unchanged. An already captured callback may run once; retain session/HUD guards.</summary>
        void CancelPending();
        /// <summary>Enables one callback per rendered frame; initially false. Disabling preserves separately pending requests. Changes during a frame affect the next captured frame.</summary>
        void SetContinuous(bool enabled);
    }
}
