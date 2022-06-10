﻿using NuCore.Utilities;
using System.Diagnostics;
using System.Net.Http.Headers;

namespace SymX
{
    /// <summary>
    /// TaskManager
    /// 
    /// SymX's state machine - handles all of the tasks that SymX must perform
    /// </summary>
    public static class TaskManager
    {
        /// <summary>
        /// The list of tasks that need to run in this session.
        /// </summary>
        public static List<Tasks> TaskList { get; set; }

        /// <summary>
        /// Private: List of generated URLs
        /// </summary>
        private static List<string> UrlList { get; set; }

        /// <summary>
        /// Private: HTTP client used for sending requests
        /// </summary>
        private static HttpClient httpClient = new HttpClient();

        /// <summary>
        /// Private: Timer used for measuring how long a download took
        /// </summary>
        private static Stopwatch timer = new Stopwatch();

        /// <summary>
        /// Magic number for how much Microsoft pads their SizeOfImage values to.
        /// </summary>
        private static ulong IMAGESIZE_PADDING = 0x1000;

        /// <summary>
        /// Default filename used for logging successful URLs.
        /// </summary>
        private static string DEFAULT_TEMP_FILE_NAME = "SuccessfulURLs.log";

        /// <summary>
        /// Default value for length of the progress bar.
        /// </summary>
        private static uint PROGRESS_BAR_LENGTH = 50;

        static TaskManager()
        {
            TaskList = new List<Tasks>();
            UrlList = new List<string>();
        }

        public static void GenerateListOfTasks()
        {
            if (CommandLine.Verbosity == Verbosity.Verbose) Console.WriteLine("Initialising HTTP client...");

            if (!CommandLine.GenerateCsv)
            {
                TaskList.Add(Tasks.GenerateListOfUrls);
            }
            else
            {
                TaskList.Add(Tasks.GenerateCsv);
            }

            if (!CommandLine.DontDownload
                && !CommandLine.GenerateCsv) TaskList.Add(Tasks.TryDownload);

            TaskList.Add(Tasks.Exit);
        }

        public static bool Run()
        {
            int numTasks = TaskList.Count;
            int curTask = 1;

            // delete temp file
            if (File.Exists(DEFAULT_TEMP_FILE_NAME)) File.Delete(DEFAULT_TEMP_FILE_NAME);

            // perform each task in sequence
            for (int i = 0; i < TaskList.Count; i++)
            {
                Tasks currentTask = TaskList[i];

                // set window title
                string taskString = $"Performing task {curTask}/{numTasks} ({currentTask})...";

                Console.Title = $"{SymXVersion.SYMX_APPLICATION_NAME} - {taskString}";

                if (CommandLine.Verbosity >= Verbosity.Normal) NCLogging.Log(taskString);
                curTask++;

                // perform the current task
                switch (currentTask)
                {
                    // Generate a list of URLs from comman-dline options or CSV.
                    case Tasks.GenerateListOfUrls:
                        UrlList = GenerateUrlList();
                        continue;
                    // Try and download the URL list generated by GenerateListOfUrls.
                    case Tasks.TryDownload:
                        List<string> successfulUrls = ScanForFiles();
                        if (successfulUrls.Count > 0)
                        {
                            if (!DownloadSuccessfulFiles(successfulUrls)) NCLogging.Log("An error occurred downloading files!\n", ConsoleColor.Red);
                        }
                        continue;
                    // Generate a CSV file from a folder.
                    case Tasks.GenerateCsv:
                        if (!MassView.Run()) NCLogging.Log("MassView failed to generate CSV file!", ConsoleColor.Red);
                        continue;
                    // Exit the program.
                    case Tasks.Exit:
                        //if (File.Exists(DEFAULT_TEMP_FILE_NAME)) File.Delete(DEFAULT_TEMP_FILE_NAME);
                        Environment.Exit(0);
                        continue;
                }

            }

            return (TaskList.Count > 0);  // if there are no remaining tasks return false.
        }

        private static List<string> GenerateUrlList()
        {
            List<string> urlList = new List<string>();

            if (CommandLine.InFile == null)
            {
                if (CommandLine.ImageSizeMin == 0
                    || CommandLine.ImageSizeMax == 0)
                {
                    for (ulong curTime = CommandLine.Start; curTime < CommandLine.End; curTime++)
                    {
                        string fileUrl = $"{CommandLine.SymbolServerUrl}/{CommandLine.FileName}/{curTime.ToString("x")}{CommandLine.ImageSize}/{CommandLine.FileName}";
                        if (CommandLine.Verbosity >= Verbosity.Verbose) Console.WriteLine(fileUrl);
                        urlList.Add(fileUrl);
                    }
                }
                else
                {
                    ulong imageSizeMin = CommandLine.ImageSizeMin;
                    ulong imageSizeMax = CommandLine.ImageSizeMax;

                    for (ulong curTime = CommandLine.Start; curTime < CommandLine.End; curTime++)
                    {
                        for (ulong curImageSize = imageSizeMin; curImageSize <= imageSizeMax; curImageSize += IMAGESIZE_PADDING)
                        {
                            string fileUrl = $"{CommandLine.SymbolServerUrl}/{CommandLine.FileName}/{curTime.ToString("x")}{curImageSize.ToString("x")}/{CommandLine.FileName}";
                            if (CommandLine.Verbosity >= Verbosity.Verbose) Console.WriteLine(fileUrl);
                            urlList.Add(fileUrl);
                        }
                    }
                }
            }
            else
            {
                // generate the URL list using massview
                return MassView.ParseUrls(CommandLine.InFile);
            }

            return urlList;
        }

        /// <summary>
        /// Tries to scan for valid files by checking every URL in the generated file list by sending a HEAD request to it. 
        /// </summary>
        /// <returns>A list of the URLs that resolved with a success code, not necessarily 200 OK.</returns>
        private static List<string> ScanForFiles()
        {
            if (CommandLine.Verbosity >= Verbosity.Verbose) NCLogging.Log("Initialising HttpClient...");

            // initialise the http client (we already instantiate it as a private field)
            // this is so we don't have to add a check for dontdownload
            httpClient.BaseAddress = new Uri("https://msdl.microsoft.com");

            // Fake a DbgX UA.
            // Just in case (thanks pivotman319)
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(CommandLine.UserAgentVendor, CommandLine.UserAgentVersion));

            StreamWriter tempFile = null;

            // create a temporary file if the user has not explicitly specified to do this
            if (!CommandLine.DontGenerateTempFile)
            {
                try
                {
                    tempFile = new StreamWriter(new FileStream(DEFAULT_TEMP_FILE_NAME, FileMode.OpenOrCreate));
                }
                catch
                {
                    NCLogging.Log("Warning: Failed to create temp file - another instance of SymX is likely running!", ConsoleColor.Yellow);
                    // don't run temp file commands to prevent crashing
                    CommandLine.DontGenerateTempFile = true;
                }
            }

            if (CommandLine.Verbosity >= Verbosity.Quiet) NCLogging.Log($"Trying {UrlList.Count} URLs...");

            timer = Stopwatch.StartNew();

            List<string> successfulUrls = new List<string>();

            int noDownloadsAtOnce = CommandLine.NumThreads;
            int failedUrls = 0; // The number of failed URLs

            // create a list of tasks
            // consider having it return the url instead
            List<Task<bool>> tasks = new List<Task<bool>>();

            // set up some temporary variables to use later
            double timeElapsed = 0;
            int numSuccessfulUrls = 0;
            double urlsPerSecond = 0;
            double percentageCompletion = 0;

            for (int curUrlSet = 0; curUrlSet < UrlList.Count; curUrlSet += noDownloadsAtOnce)
            {
                percentageCompletion = ((curUrlSet / (double)UrlList.Count)) * 100;
                string percentageCompletionString = percentageCompletion.ToString("F1");

                // Performance improvement: don't dump to the console so often (only dump it every time a set of threads complets)
                // we should allow the user to control this in future
                if (curUrlSet % noDownloadsAtOnce == 0 && CommandLine.Verbosity >= Verbosity.Normal)
                {
                    string reportString = $"{percentageCompletionString}% complete ({curUrlSet}/{UrlList.Count} URLs scanned, {failedUrls} failed), {successfulUrls.Count} files found";
                    ScanDrawDownloadUi(reportString, percentageCompletion, successfulUrls);
                }

                // Set up a batch of downloads (default 12, ~numdownloads)
                for (int curUrlInUrlSet = 0; curUrlInUrlSet < noDownloadsAtOnce; curUrlInUrlSet++)
                {
                    int curUrlId = curUrlSet + curUrlInUrlSet;

                    if (curUrlId < UrlList.Count)
                    {
                        string curUrl = UrlList[curUrlSet + curUrlInUrlSet];
                      
                        if (CommandLine.Verbosity >= Verbosity.Verbose) NCLogging.Log($"Trying URL {curUrl}...");
                        Task<bool> worker = Task<bool>.Run(() => CheckFileExists(curUrl));
                        tasks.Add(worker);
                    }
                }

                // wait for all current downloads to complete
                bool waiting = true;

                while (waiting)
                {
                    // will exit if all tasks complete
                    bool needToWait = false;

                    for (int curTask = 0; curTask < tasks.Count; curTask++)
                    {
                        Task<bool> task = tasks[curTask];

                        // we need to wait as not every task is done
                        if (!task.IsCompleted) needToWait = true;
                    }

                    waiting = needToWait;
                }

                for (int curTask = 0; curTask < tasks.Count; curTask++)
                {
                    Task<bool> task = tasks[curTask];

                    string foundUrl = UrlList[curUrlSet + curTask];
                    // it was successful so...
                    // get the current url 
                    if (task.Result) // get the current url
                    {
                        // If we haven't specified we don't want a temporary file, write it to successful_urls.log
                        if (!CommandLine.DontGenerateTempFile) tempFile.WriteLine(foundUrl);

                        if (CommandLine.Verbosity >= Verbosity.Verbose) NCLogging.Log($"Found a valid link at {foundUrl}!", ConsoleColor.Green);
                        successfulUrls.Add(foundUrl); // add it
                    }
                    else
                    {
                        // if the task caused an exception then fail checking the URL
                        if (task.IsFaulted)
                        {
                            NCLogging.Log($"An error occurred while downloading {foundUrl}!", ConsoleColor.Red);
                            failedUrls++;
                        }
                        else
                        {
                            if (CommandLine.Verbosity >= Verbosity.Verbose) NCLogging.Log($"URL not found: {foundUrl}", ConsoleColor.Yellow);
                        }
                    }
                }

                tasks.Clear();
            }

            // calculate download information
            timeElapsed = timer.ElapsedMilliseconds / 1000;
            numSuccessfulUrls = successfulUrls.Count;
            urlsPerSecond = UrlList.Count / timeElapsed;

            if (CommandLine.Verbosity >= Verbosity.Normal) NCLogging.Log($"Took {timeElapsed} seconds to check {UrlList.Count} URLs, found {numSuccessfulUrls} files ({urlsPerSecond.ToString("F1")} URLs per second)");

            if (!CommandLine.DontGenerateTempFile)
            {
                // delete SuccessfulURLs.log if we created it
                tempFile.Close();

            }

            return successfulUrls;
        }

        private static void ScanDrawDownloadUi(string reportString, double percentageCompletion, List<string> successfulUrls)
        {
            // don't log this (nucore will allow optional logging)
            if (CommandLine.Verbosity < Verbosity.Verbose)
            {
                // the reason that there is an empty catch block here
                // is that console.clear throws an exception if the console is piped to a file
                // so for some reason if someone was piping symx to a file...
                try
                {
                    // clear the *ENTIRE* console, not just visible stuff. this fixes display issues
                    // BUT may cause garbage <Win10 1507. We have to use a VTS here for now because Console doesn't have this functionality
                    Console.Clear();
                    Console.Write($"\x1b[3J"); // clear console when not in verbose mode
                }
                catch { };

                string clearCurrentLineString = "\x1b[2K";

                foreach (string successfulUrl in successfulUrls) NCLogging.Log(successfulUrl);

                // draw it last so we draw over the top of the successful urls if necessary so the user can always see the progress

                Console.SetCursorPosition(0, 0);

                // clear current line 
                Console.Write(clearCurrentLineString);

                Console.WriteLine(reportString);

                // clear current line again
                Console.Write(clearCurrentLineString);

                int numberOfBarsToDraw = (int)(PROGRESS_BAR_LENGTH * (percentageCompletion / 100));

                for (int curPercent = 0; curPercent < numberOfBarsToDraw; curPercent++)
                {
                    // large box character dec:219 hex:DB
                    Console.Write("█");
                }

                for (int curBar = numberOfBarsToDraw; curBar < (PROGRESS_BAR_LENGTH - 1); curBar++) Console.Write(" ");

                Console.Write("█\n\n");

                // start at (0, 2) so that this is always visible
                Console.SetCursorPosition(0, 2);

                // clear current line again. this will be in nucore later on
                Console.Write(clearCurrentLineString);

                NCLogging.Log("Latest successful URLs (SuccessfulURLs.log contains all successful URLs):");
            }
            else
            {
                NCLogging.Log(reportString);
            }
            
        }

        /// <summary>
        /// Try and download a file.
        /// </summary>
        /// <param name="fileName">A URI to try and download.</param>
        /// <returns>A boolean determining if the file downloaded successfully. It will return false and <see cref="Task.IsFaulted"/> will be true if an exception occurred.</returns>
        private static bool CheckFileExists(string fileName)
        {
            try
            {
                HttpRequestMessage headRequest = new HttpRequestMessage(HttpMethod.Head, fileName);
                HttpResponseMessage responseMsg = httpClient.Send(headRequest);

                return responseMsg.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static bool DownloadSuccessfulFiles(List<string> urls)
        {
            try
            {
                int numOfRetries = 0;
                int numFailedUrls = 0;

                if (CommandLine.Verbosity >= Verbosity.Verbose) Console.Clear(); // clear console

                if (CommandLine.Verbosity >= Verbosity.Normal) NCLogging.Log($"Downloading {urls.Count} successful URLs...");

                for (int curUrl = 0; curUrl < urls.Count; curUrl++)
                {
                    string url = urls[curUrl];

                    string outFileName = CommandLine.OutFile;

                    int urlId = 0;
                    string inFileName = null;

                    string[] fileNameSplit = url.Split('/');

                    // get the last section of the path (the filename)
                    inFileName = fileNameSplit[fileNameSplit.Length - 1];

                    // prevent downloading the same file several times 
                    if (urls.Count > 1)
                    {
                        urlId = curUrl + 1;

                        outFileName = $"{urlId}_{inFileName}";
                    }

                    // Prepend the output folder.
                    outFileName = $"{CommandLine.OutFolder}\\{outFileName}";

                    // Prevent files with the same number and filename in the folder overwriing each other.
                    // If the filename exists, increment it.
                    while (File.Exists(outFileName))
                    {
                        urlId++;
                        outFileName = $"{CommandLine.OutFolder}\\{urlId}_{inFileName}";
                    }

                    if (CommandLine.Verbosity >= Verbosity.Normal) NCLogging.Log($"Downloading {url} to {outFileName}...");

                    // Perform the download.

                    try
                    {
                        DownloadSuccessfulFile(url, outFileName);
                        // reset the number of retries for each file
                        numOfRetries = 0;
                    }
                    catch
                    {
                        if (numOfRetries > CommandLine.MaxRetries)
                        {
                            // reset the number of retries. we will skip the url by doing this
                            NCLogging.Log($"Reached {CommandLine.MaxRetries} tries, giving up on {url}...", ConsoleColor.Red);
                            numFailedUrls++;
                            numOfRetries = 0;
                        }
                        else
                        {
                            NCLogging.Log($"An error occurred while downloading. Retrying ({numOfRetries}/{CommandLine.MaxRetries})...", ConsoleColor.Yellow);
                            // decrement curURL to retry the current URL
                            curUrl--;
                            numOfRetries++;
                        }

                        continue;
                    }

                }

                if (numFailedUrls > 0) NCLogging.Log($"{numFailedUrls} URLs failed to download!", ConsoleColor.Yellow);
                return true;
            }
            catch (Exception ex)
            {
                NCLogging.Log($"A fatal error occurred while downloading files: {ex}", ConsoleColor.Red);
                return false;
            }
        }

        private static void DownloadSuccessfulFile(string url, string outFileName)
        {
            var stream = httpClient.GetByteArrayAsync(url);

            // Wait for download to complete (we do this basically synchronously to reduce server load)
            // Get a stream of the file 
            while (!stream.IsCompleted) { };

            using (FileStream fileStream = new FileStream(outFileName, FileMode.Create))
            {
                fileStream.Write(stream.Result);
            }
        }
    }
}
