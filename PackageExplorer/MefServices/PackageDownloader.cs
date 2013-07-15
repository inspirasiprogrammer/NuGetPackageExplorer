﻿using System;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using NuGet;
using NuGetPackageExplorer.Types;
using Ookii.Dialogs.Wpf;
using Constants = PackageExplorerViewModel.Constants;

namespace PackageExplorer
{
    using HttpClient = System.Net.Http.HttpClient;

    [Export(typeof(IPackageDownloader))]
    internal class PackageDownloader : IPackageDownloader
    {
        private ProgressDialog _progressDialog;

        [Import]
        public Lazy<MainWindow> MainWindow { get; set; }

        [Import]
        public IUIServices UIServices { get; set; }

        #region IPackageDownloader Members

        public async Task<IPackage> Download(Uri downloadUri, string packageId, SemanticVersion packageVersion)
        {
            string progressDialogText = Resources.Resources.Dialog_DownloadingPackage;
            if (!string.IsNullOrEmpty(packageId))
            {
                progressDialogText = String.Format(CultureInfo.CurrentCulture, progressDialogText, packageId,
                                                   packageVersion);
            }
            else
            {
                progressDialogText = String.Format(CultureInfo.CurrentCulture, progressDialogText, downloadUri,
                                                   String.Empty);
            }

            _progressDialog = new ProgressDialog
                              {
                                  Text = progressDialogText,
                                  WindowTitle = Resources.Resources.Dialog_Title,
                                  ShowTimeRemaining = true,
                                  CancellationText = "Canceling download..."
                              };
            _progressDialog.ShowDialog(MainWindow.Value);

            // polling for Cancel button being clicked
            var cts = new CancellationTokenSource();
            var timer = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(200)
                        };
            timer.Tick += (o, e) =>
                          {
                              if (_progressDialog.CancellationPending)
                              {
                                  timer.Stop();
                                  cts.Cancel();
                              }
                          };
            timer.Start();

            // report progress must be done via UI thread
            Action<int, string> reportProgress =
                (percent, description) => _progressDialog.ReportProgress(percent, null, description);

            try
            {
                IPackage package = await DownloadData(downloadUri, reportProgress, cts.Token);
                return package;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception exception)
            {
                OnError(exception);
                return null;
            }
            finally
            {
                timer.Stop();

                // close progress dialog when done
                _progressDialog.Close();
                _progressDialog = null;
                MainWindow.Value.Activate();
            }
        }

        #endregion

        private async Task<IPackage> DownloadData(Uri url, Action<int, string> reportProgressAction, CancellationToken cancelToken)
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(HttpUtility.CreateUserAgentString(Constants.UserAgentClient));

            using (HttpResponseMessage response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancelToken))
            {
                using (Stream responseStream = await response.Content.ReadAsStreamAsync())
                {
                    const int chunkSize = 4 * 1024;
                    var totalBytes = (int)(response.Content.Headers.ContentLength ?? 0);
                    var buffer = new byte[chunkSize];
                    int readSoFar = 0;

                    // while reading data from network, we write it to a temp file
                    string tempFilePath = Path.GetTempFileName();
                    using (FileStream fileStream = File.OpenWrite(tempFilePath))
                    {
                        while (readSoFar < totalBytes)
                        {
                            int bytesRead = await responseStream.ReadAsync(buffer, 0, Math.Min(chunkSize, totalBytes - readSoFar), cancelToken);
                            readSoFar += bytesRead;

                            cancelToken.ThrowIfCancellationRequested();

                            fileStream.Write(buffer, 0, bytesRead);
                            OnProgress(readSoFar, totalBytes, reportProgressAction);
                        }
                    }

                    // read all bytes successfully
                    if (readSoFar >= totalBytes)
                    {
                        return new ZipPackage(tempFilePath);
                    }
                }
            }
            return null;
        }

        private void OnError(Exception error)
        {
            UIServices.Show((error.InnerException ?? error).Message, MessageLevel.Error);
        }

        private void OnProgress(int bytesReceived, int totalBytes, Action<int, string> reportProgress)
        {
            int percentComplete = (bytesReceived * 100) / totalBytes;
            string description = String.Format("Downloaded {0}KB of {1}KB...", ToKB(bytesReceived), ToKB(totalBytes));
            reportProgress(percentComplete, description);
        }

        private long ToKB(long totalBytes)
        {
            return (totalBytes + 1023) / 1024;
        }
    }
}