using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Steamworks;

namespace XCOM2Launcher.Steam
{
    public static class Workshop
    {
        public const string APPID_FILENAME = "steam_appid.txt";

        /// <summary>
        /// According to Steamworks API constant kNumUGCResultsPerPage.
        /// The maximum number of results that you'll receive for a query result.
        /// </summary>
        public const int MAX_UGC_RESULTS = 50; // according to 

        static Workshop()
        {
            SteamManager.EnsureInitialized();
            _downloadItemCallback = Callback<DownloadItemResult_t>.Create(result => OnItemDownloaded?.Invoke(null, new DownloadItemEventArgs() { Result = result}));
        }
        
        public static ulong[] GetSubscribedItems()
        {
            var num = SteamUGC.GetNumSubscribedItems();
            var ids = new PublishedFileId_t[num];
            SteamUGC.GetSubscribedItems(ids, num);

            return ids.Select(t => t.m_PublishedFileId).ToArray();
        }

        public static void Subscribe(ulong id)
        {
            SteamUGC.SubscribeItem(id.ToPublishedFileID());
        }

        public static void Unsubscribe(ulong id)
        {
            SteamUGC.UnsubscribeItem(id.ToPublishedFileID());
        }

        /// <summary>
        /// Returns the UGC Details for the specified workshop id.
        /// </summary>
        /// <param name="id">Workshop id</param>
        /// <param name="getFullDescription">Sets whether to return the full description for the item. If set to false, the description is truncated at 255 bytes.</param>
        /// <returns>The requested data or the default struct (check for m_eResult == EResultNone), if the request failed.</returns>
        public static async Task<SteamUGCDetails_t> GetDetailsAsync(ulong id, bool getFullDescription = false)
        {
            var result = await GetDetailsAsync(new List<ulong> {id}, getFullDescription);
            return result?.FirstOrDefault() ?? new SteamUGCDetails_t();
        }

        /// <summary>
        /// Returns a list of UGC Details for the specified workshop id's.
        /// </summary>
        /// <param name="identifiers">Workshop id's</param>
        /// <param name="getFullDescription">Sets whether to return the full description for the item. If set to false, the description is truncated at 255 bytes.</param>
        /// <returns>The requested data or null, if the request failed.</returns>
        public static async Task<List<SteamUGCDetails_t>> GetDetailsAsync(List<ulong> identifiers, bool getFullDescription = false)
        {
            if (identifiers == null)
                throw new ArgumentNullException(nameof(identifiers));

            if (identifiers.Count > MAX_UGC_RESULTS)
                throw new ArgumentException($"Max allowed number of identifiers is {MAX_UGC_RESULTS}.");

            if (!SteamManager.IsSteamRunning()) return null;

            var idList = identifiers
                .Where(x => x > 0)
                .Distinct()
                .Select(x => new PublishedFileId_t(x))
                .ToArray();
            if (idList.Length == 0) return new List<SteamUGCDetails_t>();
            
            var queryHandle = SteamUGC.CreateQueryUGCDetailsRequest(idList, (uint)idList.Length);
            SteamUGC.SetReturnLongDescription(queryHandle, getFullDescription);
            SteamUGC.SetReturnChildren(queryHandle, true); // required, otherwise m_unNumChildren will always be 0
            
            var apiCall = SteamUGC.SendQueryUGCRequest(queryHandle);

            var results = await SteamManager.QueryResultAsync<SteamUGCQueryCompleted_t, List<SteamUGCDetails_t>>(apiCall,
                (result, ioFailure) =>
                {
                    var details = new List<SteamUGCDetails_t>();

                    for (uint i = 0; i < result.m_unNumResultsReturned; i++)
                    {
                        // Retrieve Value
                        if (SteamUGC.GetQueryUGCResult(queryHandle, i, out var detail))
                        {
                            details.Add(detail);
                        }
                    }

                    SteamUGC.ReleaseQueryUGCRequest(queryHandle);
                    return details;
                });
            return results;
        }

        private static async Task<ulong[]> GetDependenciesAsync(ulong workShopId, uint dependencyCount)
        {
            if (dependencyCount <= 0) return Array.Empty<ulong>();
            if (workShopId <= 0) return Array.Empty<ulong>();
            if (!SteamManager.IsSteamRunning()) return Array.Empty<ulong>();

            var queryHandle = SteamUGC.CreateQueryUGCDetailsRequest(new[] { workShopId.ToPublishedFileID() }, 1);
            SteamUGC.SetReturnChildren(queryHandle, true);
            var apiCall = SteamUGC.SendQueryUGCRequest(queryHandle);

            var results = await SteamManager.QueryResultAsync<SteamUGCQueryCompleted_t, ulong[]>(apiCall, (result, ioFailure) =>
            {
                // todo: implement paged results, max is 50, see: https://partner.steamgames.com/doc/api/ISteamUGC#kNumUGCResultsPerPage
                var idList = new PublishedFileId_t[dependencyCount];
                var success = SteamUGC.GetQueryUGCChildren(queryHandle, 0, idList, (uint)idList.Length);

                if (!success) return Array.Empty<ulong>();
                
                var resultIds = idList.Select(item => item.m_PublishedFileId).ToArray();
                return resultIds;

            });

            return results;
        }

        public static Task<ulong[]> GetDependenciesAsync(SteamUGCDetails_t details)
        {
            return GetDependenciesAsync(details.m_nPublishedFileId.m_PublishedFileId, details.m_unNumChildren);
        }

        public static EItemState GetDownloadStatus(ulong id)
        {
            return (EItemState)SteamUGC.GetItemState(new PublishedFileId_t(id));
        }

        public static InstallInfo GetInstallInfo(ulong id)
        {
            ulong punSizeOnDisk;
            string pchFolder;
            uint punTimeStamp;

            SteamUGC.GetItemInstallInfo(new PublishedFileId_t(id), out punSizeOnDisk, out pchFolder, 256, out punTimeStamp);

            return new InstallInfo
            {
                ItemID = id,
                SizeOnDisk = punSizeOnDisk,
                Folder = pchFolder,
                TimeStamp = new DateTime(punTimeStamp * 10)
            };
        }

        public static UpdateInfo GetDownloadInfo(ulong id)
        {
            ulong punBytesProcessed;
            ulong punBytesTotal;

            SteamUGC.GetItemDownloadInfo(new PublishedFileId_t(id), out punBytesProcessed, out punBytesTotal);

            return new UpdateInfo
            {
                ItemID = id,
                BytesProcessed = punBytesProcessed,
                BytesTotal = punBytesTotal
            };
        }


        #region Download Item
        public class DownloadItemEventArgs : EventArgs
        {
            public DownloadItemResult_t Result { get; set; }
        }

        // ReSharper disable once NotAccessedField.Local
        private static Callback<DownloadItemResult_t> _downloadItemCallback;
        public delegate void DownloadItemHandler(object sender, DownloadItemEventArgs e);
        public static event DownloadItemHandler OnItemDownloaded;
        public static void DownloadItem(ulong id)
        {
            SteamUGC.DownloadItem(new PublishedFileId_t(id), true);
        }

        #endregion

        public static string GetUsername(ulong steamID)
        {
            var work_done = new System.Threading.ManualResetEventSlim(false);
            using (Callback<PersonaStateChange_t>.Create(result => { work_done.Set(); }))
            {
                bool success = SteamFriends.RequestUserInformation(new CSteamID(steamID), true);
                if (success)
                {
                    work_done.Wait(5000);
                    work_done.Reset();
                    return SteamFriends.GetFriendPersonaName(new CSteamID(steamID)) ?? string.Empty;
                }

                return string.Empty;
            }
        }
    }
}