﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Input;
using Windows.UI.Xaml;
using Microsoft.WindowsAPICodePack.Dialogs.Controls;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using ReactiveUI;
using Wabbajack.Common;
using Wabbajack.Lib.Validation;
using File = Alphaleonis.Win32.Filesystem.File;
using Game = Wabbajack.Common.Game;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace Wabbajack.Lib.Downloaders
{
    public class BethesdaNetDownloader : IUrlDownloader, INeedsLogin
    {
        public const string DataName = "BethesdaNetData";
        public BethesdaNetDownloader()
        {
            TriggerLogin = ReactiveCommand.Create(async () => await Utils.Log(new RequestBethesdaNetLogin()).Task, IsLoggedIn.Select(b => !b).ObserveOn(RxApp.MainThreadScheduler));
            ClearLogin = ReactiveCommand.Create(() => Utils.DeleteEncryptedJson(DataName), IsLoggedIn.ObserveOn(RxApp.MainThreadScheduler));

            if (File.Exists("bethnetlogin.exe")) return;

            using (var os = File.OpenWrite("bethnetlogin.exe"))
            using (var i = Assembly.GetExecutingAssembly().GetManifestResourceStream("Wabbajack.Lib.Downloaders.BethesdaNet.bethnetlogin.exe"))
            {
                i.CopyTo(os);
            }


        }

        public async Task<AbstractDownloadState> GetDownloaderState(dynamic archiveINI)
        {
            var url = (Uri)DownloaderUtils.GetDirectURL(archiveINI);
            if (url != null && url.Host == "bethesda.net" && url.AbsolutePath.StartsWith("/en/mods/"))
            {
                var split = url.AbsolutePath.Split('/');
                var game = split[3];
                var modId = split[5];
                return new State {GameName = game, ContentId = modId};
            }

            return null;
        }

        public async Task Prepare()
        {
            if (Utils.HaveEncryptedJson(DataName)) return;
            await Utils.Log(new RequestBethesdaNetLogin()).Task;
        }

        public static async Task<BethesdaNetData> Login()
        {
            var game = Path.Combine(Game.SkyrimSpecialEdition.MetaData().GameLocation(), "SkyrimSE.exe");
            var info = new ProcessStartInfo();
            info.FileName = "bethnetlogin.exe";
            info.Arguments = $"\"{game}\" SkyrimSE.exe";
            info.RedirectStandardError = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            var process = Process.Start(info);
            ChildProcessTracker.AddProcess(process);
            string last_line = "";

            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync();
                if (line == null) break;
                last_line = line;
            }

            var result =  last_line.FromJSONString<BethesdaNetData>();
            result.ToEcryptedJson(DataName);
            return result;
        }

        public AbstractDownloadState GetDownloaderState(string url)
        {
            throw new NotImplementedException();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public ICommand TriggerLogin { get; }
        public ICommand ClearLogin { get; }
        public IObservable<bool> IsLoggedIn => Utils.HaveEncryptedJsonObservable(DataName);
        public string SiteName => "Bethesda.NET";
        public string MetaInfo => "Wabbajack will start the game, then exit once you enter the Mods page";
        public Uri SiteURL => new Uri("https://bethesda.net");
        public Uri IconUri { get; }

        public class State : AbstractDownloadState
        {
            public string GameName { get; set; }
            public string ContentId { get; set; }
            public override bool IsWhitelisted(ServerWhitelist whitelist)
            {
                return true;
            }

            public override async Task Download(Archive a, string destination)
            {
                var (client, info, collected) = await ResolveDownloadInfo();
                using (var file = File.OpenWrite(destination))
                {

                    var max_chunks = info.depot_list[0].file_list[0].chunk_count;
                    foreach (var chunk in info.depot_list[0].file_list[0].chunk_list.OrderBy(c => c.index))
                    {
                        Utils.Status($"Downloading {a.Name}", chunk.index * 100 / max_chunks);
                        var got = await client.GetAsync(
                            $"https://content.cdp.bethesda.net/{collected.CDPProductId}/{collected.CDPPropertiesId}/{chunk.sha}");
                        var data = await got.Content.ReadAsByteArrayAsync();
                        AESCTRDecrypt(collected.AESKey, collected.AESIV, data);
                        
                        if (chunk.uncompressed_size == chunk.chunk_size)
                            await file.WriteAsync(data, 0, data.Length);
                        else
                        {
                            using (var ms = new MemoryStream(data))
                            using (var zlibStream = new ICSharpCode.SharpZipLib.Zip.Compression.Streams.InflaterInputStream(ms))
                                await zlibStream.CopyToAsync(file);
                        }
                    }
                }
            }

            public override async Task<bool> Verify()
            {
                var info = await ResolveDownloadInfo();
                return true;
            }

            private async Task<(HttpClient, CDPTree, CollectedBNetInfo)> ResolveDownloadInfo()
            {
                var info = new CollectedBNetInfo();

                var login_info = Utils.FromEncryptedJson<BethesdaNetData>(DataName);

                var client = new HttpClient();

                client.BaseAddress = new Uri("https://api.bethesda.net");
                client.DefaultRequestHeaders.Add("User-Agent", "bnet");
                foreach (var header in login_info.headers.Where(h => h.Key.ToLower().StartsWith("x-")))
                    client.DefaultRequestHeaders.Add(header.Key, header.Value);

                var posted = await client.PostAsync("/beam/accounts/external_login",
                    new StringContent(login_info.body, Encoding.UTF8, "application/json"));

                info.AccessToken = (await posted.Content.ReadAsStringAsync()).FromJSONString<BeamLoginResponse>().access_token;

                client.DefaultRequestHeaders.Add("x-cdp-app", "UGC SDK");
                client.DefaultRequestHeaders.Add("x-cdp-app-ver", "0.9.11314/debug");
                client.DefaultRequestHeaders.Add("x-cdp-lib-ver", "0.9.11314/debug");
                client.DefaultRequestHeaders.Add("x-cdp-platform","Win/32");

                posted = await client.PostAsync("cdp-user/auth",
                    new StringContent("{\"access_token\": \"" + info.AccessToken + "\"}", Encoding.UTF8,
                        "application/json"));
                info.CDPToken = (await posted.Content.ReadAsStringAsync()).FromJSONString<CDPLoginResponse>().token;

                client.DefaultRequestHeaders.Add("X-Access-Token", info.AccessToken);
                var got = await client.GetAsync($"mods/ugc-workshop/content/get?content_id={ContentId}");
                JObject data = JObject.Parse(await got.Content.ReadAsStringAsync());

                var content = data["platform"]["response"]["content"];

                info.CDPBranchId = (int)content["cdp_branch_id"];
                info.CDPProductId = (int)content["cdp_product_id"];

                client.DefaultRequestHeaders.Add("Authorization", $"Token {info.CDPToken}");

                got = await client.GetAsync(
                    $"/cdp-user/projects/{info.CDPProductId}/branches/{info.CDPBranchId}/tree/.json");

                var tree = (await got.Content.ReadAsStringAsync()).FromJSONString<CDPTree>();

                got = await client.GetAsync(
                    $"/cdp-user/projects/{info.CDPProductId}/branches/{info.CDPBranchId}/depots/.json");

                var props_obj = JObject.Parse(await got.Content.ReadAsStringAsync()).Properties().First();
                info.CDPPropertiesId = (int)props_obj.Value["properties_id"];
                info.AESKey = props_obj.Value["ex_info_A"].Select(e => (byte)e).ToArray();
                info.AESIV = props_obj.Value["ex_info_B"].Select(e => (byte)e).Take(16).ToArray();
                
                return (client, tree, info);
            }
            
            static int AESCTRDecrypt(byte[] Key, byte[] IV, byte[] Data)
            {
                IBufferedCipher cipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");
                cipher.Init(false, new ParametersWithIV(ParameterUtilities.CreateKeyParameter("AES", Key), IV));

                return cipher.DoFinal(Data, Data, 0);
            }

            public override IDownloader GetDownloader()
            {
                throw new NotImplementedException();
            }

            public override string GetReportEntry(Archive a)
            {
                throw new NotImplementedException();
            }



            private class BeamLoginResponse
            {
                public string access_token { get; set; }

            }

            private class CDPLoginResponse
            {
                public string token { get; set; }
            }

            private class CollectedBNetInfo
            {
                public byte[] AESKey { get; set; }
                public byte[] AESIV { get; set; }
                public string AccessToken { get; set; }
                public string CDPToken { get; set; }
                public int CDPBranchId { get; set; }
                public int CDPProductId { get; set; }
                public int CDPPropertiesId { get; set; }
            }

            public class CDPTree
            {
                public List<Depot> depot_list { get; set; }

                public class Depot
                {
                    public List<CDPFile> file_list { get; set; }

                    public class CDPFile
                    {
                        public int chunk_count { get; set; }
                        public List<Chunk> chunk_list { get; set; }

                        public string name { get; set; }

                        public class Chunk
                        {
                            public int chunk_size { get; set; }
                            public int index { get; set; }
                            public string sha { get; set; }
                            public int uncompressed_size { get; set; }
                        }
                    }
                }
            }

        }
    }

    internal class DownloadInfo
    {
    }

    public class RequestBethesdaNetLogin : AUserIntervention
    {
        public override string ShortDescription => "Getting LoversLab information";
        public override string ExtendedDescription { get; }

        private readonly TaskCompletionSource<BethesdaNetData> _source = new TaskCompletionSource<BethesdaNetData>();
        public Task<BethesdaNetData> Task => _source.Task;

        public void Resume(BethesdaNetData data)
        {
            Handled = true;
            _source.SetResult(data);
        }

        public override void Cancel()
        {
            Handled = true;
            _source.SetCanceled();
        }

    }

    public class BethesdaNetData
    {
        public string body { get; set; }
        public Dictionary<string, string> headers = new Dictionary<string, string>();
    }

}
