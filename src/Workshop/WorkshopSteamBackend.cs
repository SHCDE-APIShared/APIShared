using Steamworks;
using System;

namespace APIShared.Workshop
{
    // SDK adapter is shared by both publishers; offline tests override this boundary.
    internal class WorkshopSteamBackend
    {
        internal virtual WorkshopSteamResult<T> CreateResult<T>(Action<T, bool> action) where T : struct => new SdkResult<T>(action);
        private sealed class SdkResult<T> : WorkshopSteamResult<T> where T : struct
        {
            private readonly CallResult<T> result;
            internal SdkResult(Action<T, bool> action) { result = CallResult<T>.Create((value, io) => action(value, io)); }
            internal override void Set(SteamAPICall_t call) { result.Set(call); }
        }
        internal virtual void ActivateGameOverlayToWebPage(string pchURL, EActivateGameOverlayToWebPageMode eMode = default(EActivateGameOverlayToWebPageMode)) => SteamFriends.ActivateGameOverlayToWebPage(pchURL, eMode);
        internal virtual SteamAPICall_t AddDependency(PublishedFileId_t nParentPublishedFileID, PublishedFileId_t nChildPublishedFileID) => SteamUGC.AddDependency(nParentPublishedFileID, nChildPublishedFileID);
        internal virtual SteamAPICall_t CreateItem(AppId_t nConsumerAppId, EWorkshopFileType eFileType) => SteamUGC.CreateItem(nConsumerAppId, eFileType);
        internal virtual UGCQueryHandle_t CreateQueryUGCDetailsRequest(PublishedFileId_t[] pvecPublishedFileID, uint unNumPublishedFileIDs) => SteamUGC.CreateQueryUGCDetailsRequest(pvecPublishedFileID, unNumPublishedFileIDs);
        internal virtual UGCQueryHandle_t CreateQueryUserUGCRequest(AccountID_t unAccountID, EUserUGCList eListType, EUGCMatchingUGCType eMatchingUGCType, EUserUGCListSortOrder eSortOrder, AppId_t nCreatorAppID, AppId_t nConsumerAppID, uint unPage) => SteamUGC.CreateQueryUserUGCRequest(unAccountID, eListType, eMatchingUGCType, eSortOrder, nCreatorAppID, nConsumerAppID, unPage);
        internal virtual bool GetItemInstallInfo(PublishedFileId_t nPublishedFileID, out ulong punSizeOnDisk, out string pchFolder, uint cchFolderSize, out uint punTimeStamp) => SteamUGC.GetItemInstallInfo(nPublishedFileID, out punSizeOnDisk, out pchFolder, cchFolderSize, out punTimeStamp);
        internal virtual uint GetItemState(PublishedFileId_t nPublishedFileID) => SteamUGC.GetItemState(nPublishedFileID);
        internal virtual EItemUpdateStatus GetItemUpdateProgress(UGCUpdateHandle_t handle, out ulong punBytesProcessed, out ulong punBytesTotal) => SteamUGC.GetItemUpdateProgress(handle, out punBytesProcessed, out punBytesTotal);
        internal virtual bool GetQueryUGCChildren(UGCQueryHandle_t handle, uint index, PublishedFileId_t[] pvecPublishedFileID, uint cMaxEntries) => SteamUGC.GetQueryUGCChildren(handle, index, pvecPublishedFileID, cMaxEntries);
        internal virtual bool GetQueryUGCMetadata(UGCQueryHandle_t handle, uint index, out string pchMetadata, uint cchMetadatasize) => SteamUGC.GetQueryUGCMetadata(handle, index, out pchMetadata, cchMetadatasize);
        internal virtual bool GetQueryUGCResult(UGCQueryHandle_t handle, uint index, out SteamUGCDetails_t pDetails) => SteamUGC.GetQueryUGCResult(handle, index, out pDetails);
        internal virtual bool ReleaseQueryUGCRequest(UGCQueryHandle_t handle) => SteamUGC.ReleaseQueryUGCRequest(handle);
        internal virtual SteamAPICall_t RemoveDependency(PublishedFileId_t nParentPublishedFileID, PublishedFileId_t nChildPublishedFileID) => SteamUGC.RemoveDependency(nParentPublishedFileID, nChildPublishedFileID);
        internal virtual SteamAPICall_t SendQueryUGCRequest(UGCQueryHandle_t handle) => SteamUGC.SendQueryUGCRequest(handle);
        internal virtual bool SetItemContent(UGCUpdateHandle_t handle, string pszContentFolder) => SteamUGC.SetItemContent(handle, pszContentFolder);
        internal virtual bool SetItemDescription(UGCUpdateHandle_t handle, string pchDescription) => SteamUGC.SetItemDescription(handle, pchDescription);
        internal virtual bool SetItemMetadata(UGCUpdateHandle_t handle, string pchMetaData) => SteamUGC.SetItemMetadata(handle, pchMetaData);
        internal virtual bool SetItemPreview(UGCUpdateHandle_t handle, string pszPreviewFile) => SteamUGC.SetItemPreview(handle, pszPreviewFile);
        internal virtual bool SetItemTags(UGCUpdateHandle_t updateHandle, System.Collections.Generic.IList<string> pTags, bool bAllowAdminTags = default(bool)) => SteamUGC.SetItemTags(updateHandle, pTags, bAllowAdminTags);
        internal virtual bool SetItemTitle(UGCUpdateHandle_t handle, string pchTitle) => SteamUGC.SetItemTitle(handle, pchTitle);
        internal virtual bool SetItemVisibility(UGCUpdateHandle_t handle, ERemoteStoragePublishedFileVisibility eVisibility) => SteamUGC.SetItemVisibility(handle, eVisibility);
        internal virtual bool SetReturnChildren(UGCQueryHandle_t handle, bool bReturnChildren) => SteamUGC.SetReturnChildren(handle, bReturnChildren);
        internal virtual bool SetReturnMetadata(UGCQueryHandle_t handle, bool bReturnMetadata) => SteamUGC.SetReturnMetadata(handle, bReturnMetadata);
        internal virtual UGCUpdateHandle_t StartItemUpdate(AppId_t nConsumerAppId, PublishedFileId_t nPublishedFileID) => SteamUGC.StartItemUpdate(nConsumerAppId, nPublishedFileID);
        internal virtual SteamAPICall_t SubmitItemUpdate(UGCUpdateHandle_t handle, string pchChangeNote) => SteamUGC.SubmitItemUpdate(handle, pchChangeNote);
        internal virtual CSteamID GetSteamID() => SteamUser.GetSteamID();
    }
    internal abstract class WorkshopSteamResult<T> where T : struct
    {
        internal abstract void Set(SteamAPICall_t call);
    }
}
