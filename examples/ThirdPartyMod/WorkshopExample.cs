using APIShared;
using APIShared.Workshop;
using System;
using System.Threading.Tasks;

namespace ThirdPartyMod
{
    // Invoke from a Unity-thread UI/event after Steam initialized. This does not depend on native API readiness.
    internal static class WorkshopExample
    {
        internal static async Task PreparePublishing(ModApiClient api, string preparedDirectory, string previewFile,
            string version, Action<WorkshopUploadPlan, Action> showReview, Action<WorkshopOperation> observe)
        {
            var options = new WorkshopUploadOptions {
                SourceDirectory = preparedDirectory, PreviewFile = previewFile,
                Title = "Example mod", Description = "Example Workshop package", Visibility = "Private",
                Draft = new WorkshopPackageDraft("ExampleModPackage", version, "0.7.1", "2.14.0")
            };
            var plan = await api.Workshop.PrepareUploadAsync(options);
            // Present plan.Files/version before enabling this confirmation action. The caller invokes it on Unity.
            showReview(plan, () => observe(api.Workshop.Publish(plan)));
        }

        internal static async Task PrepareUpdate(ModApiClient api, ulong steamItemId,
            Action<WorkshopInstallPlan, Func<Task>> showReview, Action<WorkshopOperation> observe)
        {
            var plan = await api.Workshop.PrepareInstallAsync(steamItemId, WorkshopAction.Update);
            // Display delivered/removable files and diagnostics, then wait for a deliberate user action.
            // The review UI awaits the returned task and displays any exception; avoid an unhandled async-void command.
            showReview(plan, async () => observe(await api.Workshop.ApplyAsync(plan)));
        }
    }
}
