﻿using System;
using System.Net;
using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using BunnyCDN.Net.Storage;
using CG.Web.MegaApiClient;
using FluentFTP;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Wabbajack.BuildServer.Model.Models;
using Wabbajack.BuildServer.Models.JobQueue;
using Wabbajack.Common;
using Wabbajack.Lib;
using Wabbajack.Lib.Downloaders;
using File = System.IO.File;

namespace Wabbajack.BuildServer.Models.Jobs
{
    public class UploadToCDN : AJobPayload
    {
        public override string Description => $"Push an uploaded file ({FileId}) to the CDN";
        
        public string FileId { get; set; }
        
        public override async Task<JobResult> Execute(DBContext db, SqlService sql, AppSettings settings)
        {
            var file = await db.UploadedFiles.AsQueryable().Where(f => f.Id == FileId).FirstOrDefaultAsync();
            using (var client = new FtpClient("storage.bunnycdn.com"))
            {
                client.Credentials = new NetworkCredential(settings.BunnyCDN_User, settings.BunnyCDN_Password);
                await client.ConnectAsync();
                using (var stream = File.OpenRead(Path.Combine("public", "files", file.MungedName)))
                {
                    await client.UploadAsync(stream, file.MungedName, progress: new Progress(file.MungedName));
                }
                
                await db.Jobs.InsertOneAsync(new Job
                {
                    Priority = Job.JobPriority.High,
                    Payload = new IndexJob
                    {
                        Archive = new Archive
                        {
                            Name = file.MungedName,
                            Size = file.Size,
                            Hash = file.Hash,
                            State = new HTTPDownloader.State
                            {
                                Url = file.Uri
                            }
                        }
                    }
                });
            }
            return JobResult.Success();
        }

        private class Progress : IProgress<FluentFTP.FtpProgress>
        {
            private string _name;
            private DateTime LastUpdate = DateTime.UnixEpoch;

            public Progress(string name)
            {
                _name = name;
            }
            public void Report(FtpProgress value)
            {
                if (DateTime.Now - LastUpdate <= TimeSpan.FromSeconds(5)) return;

                Utils.Log($"Uploading {_name} - {value.Progress}% {(int)((value.TransferSpeed + 1) / 1024 / 1024)} MB/sec ETA: {value.ETA}");
                LastUpdate = DateTime.Now;

            }
        }
    }
}
