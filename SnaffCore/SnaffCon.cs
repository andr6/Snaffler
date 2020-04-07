﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using SnaffCore.ComputerFind;
using SnaffCore.Concurrency;
using Classifiers;
using SnaffCore.ShareFind;
using SnaffCore.ShareScan;
using Timer = System.Timers.Timer;
using ShareResult = SnaffCore.ShareFind.ShareFinder.ShareResult;

namespace SnaffCore
{
    public class SnaffCon
    {
        private Config.Config Config { get; set; }
        private LimitedConcurrencyLevelTaskScheduler SharefinderLcts { get; set; }
        private TaskFactory SharefinderTaskFactory { get; set; }
        private CancellationTokenSource SharefinderCts { get; set; }
        private List<Task> SharefinderTasks { get; set; } = new List<Task>();
        private LimitedConcurrencyLevelTaskScheduler SharescannerLcts { get; set; }
        private TaskFactory SharescannerTaskFactory { get; set; }
        private CancellationTokenSource SharescannerCts { get; set; }
        private List<Task> SharescannerTasks { get; set; } = new List<Task>();
        private bool SysvolTaskCreated { get; set; }
        private bool NetlogonTaskCreated { get; set; }

        public SnaffCon(Config.Config conf)
        {
            Config = conf;
            SharefinderLcts = new LimitedConcurrencyLevelTaskScheduler(Config.MaxThreads);
            SharefinderTaskFactory = new TaskFactory(SharefinderLcts);
            SharefinderCts = new CancellationTokenSource();
            SharescannerLcts = new LimitedConcurrencyLevelTaskScheduler(Config.MaxThreads);
            SharescannerTaskFactory = new TaskFactory(SharescannerLcts);
            SharescannerCts = new CancellationTokenSource();
        }

        public void Execute()
        {
            var targetComputers = new List<string>();
            ConcurrentBag<ShareResult> foundShares = new ConcurrentBag<ShareResult>();

            var statusUpdateTimer =
                new Timer(TimeSpan.FromMinutes(1)
                    .TotalMilliseconds) {AutoReset = true}; // Set the time (5 mins in this case)
            statusUpdateTimer.Elapsed += StatusUpdate;
            statusUpdateTimer.Start();

            if (Config.Options.DirTarget == null)
            {
                Config.Mq.Info("Getting computers from AD.");
                // We do this single threaded cos it's fast and not easily divisible.
                var activeDirectory = new ActiveDirectory(Config);
                targetComputers = activeDirectory.DomainComputers;
                if (targetComputers == null)
                {
                    Config.Mq.Error(
                        "Something fucked out finding the computers in the domain. You must be holding it wrong.");
                    while (true)
                    {
                        Config.Mq.Terminate();
                    }
                }

                var numTargetComputers = targetComputers.Count.ToString();
                Config.Mq.Info("Got " + numTargetComputers + " computers from AD.");
                if (targetComputers.Count == 0)
                {
                    Config.Mq.Error("Didn't find any domain computers. Seems weird. Try pouring water on it.");
                    while (true)
                    {
                        Config.Mq.Terminate();
                    }
                }
            }
            else
            {
                foundShares.Add(new ShareResult(){SharePath = Config.Options.DirTarget, ScanShare = true});
            }

            if (Config.Options.ShareFinderEnabled)
            {
                Config.Mq.Info("Starting to find readable shares.");
                foreach (var computer in targetComputers)
                {
                    // ShareFinder Task Creation
                    Config.Mq.Info("Creating a sharefinder task for " + computer);
                    var t = SharefinderTaskFactory.StartNew(() =>
                    {
                        try
                        {
                            ShareFinder shareFinder = new ShareFinder();
                            List<ShareResult> shareResults = shareFinder.GetComputerShares(computer, Config);
                            foreach (ShareResult shareResult in shareResults)
                            {
                                foundShares.Add(shareResult);
                            }
                        }
                        catch (Exception e)
                        {
                            Config.Mq.Trace(e.ToString());
                        }
                    }, SharefinderCts.Token);
                    SharefinderTasks.Add(t);
                }

                Config.Mq.Info("Created all " + SharefinderTasks.Count + " sharefinder tasks.");
            }

            if (Config.Options.ShareScanEnabled)
            {
                var shareFinderTasksDone = false;
                Config.Mq.Info("Starting to search shares for files.");
                // keep going until all sharefinder tasks are completed or faulted, and there's no shares left to start scanner tasks for
                while (shareFinderTasksDone == false || !foundShares.IsEmpty)
                {
                    // check if all the shareFinder Tasks are done
                    var completedShareFinderTasks = Array.FindAll(SharefinderTasks.ToArray(),
                        element => element.Status == TaskStatus.RanToCompletion);
                    var faultedShareFinderTasks = Array.FindAll(SharefinderTasks.ToArray(),
                        element => element.Status == TaskStatus.Faulted);
                    if ((completedShareFinderTasks.Length + faultedShareFinderTasks.Length) ==
                        SharefinderTasks.Count)
                    {
                        // update the completion status.
                        shareFinderTasksDone = true;
                        Config.Mq.Info("All Sharefinder Tasks completed.");
                    }

                    //pull shares out of the result bag and make scanner tasks for them.
                    while (foundShares.TryTake(out ShareResult share))
                    {
                        string sharePath = share.SharePath;
                        if (!String.IsNullOrWhiteSpace(sharePath))
                        {
                            // handle anoying sysvol/netlogon duplication in DCs
                            if (sharePath.ToLower().EndsWith("sysvol"))
                            {
                                if (SysvolTaskCreated)
                                {
                                    continue;
                                }

                                SysvolTaskCreated = true;
                            }
                            else if (sharePath.ToLower().EndsWith("netlogon"))
                            {
                                if (NetlogonTaskCreated)
                                {
                                    continue;
                                }

                                NetlogonTaskCreated = true;
                            }

                            // check if it's an admin share
                            var isCDollarShare = sharePath.EndsWith("C$");
                            // put a result on the queue
                            Config.Mq.ShareResult(share);
                            // bail out if we're not scanning admin shares
                            if (isCDollarShare && !Config.Options.ScanCDollarShares)
                            {
                                continue;
                            }

                            // otherwise create a TreeWalker task
                            Config.Mq.Info("Creating ShareScanner for:" + share);
                            var t = SharescannerTaskFactory.StartNew(() =>
                            {
                                try
                                {
                                    var treeWalker = new TreeWalker(Config, sharePath);
                                }
                                catch (Exception e)
                                {
                                    Config.Mq.Trace(e.ToString());
                                }
                            }, SharescannerCts.Token);
                            SharescannerTasks.Add(t);
                        }
                    }
                }

                var shareScannerTasksDone = false;

                while (!shareScannerTasksDone)
                {
                    var completedShareScannerTasks = Array.FindAll(SharescannerTasks.ToArray(),
                        element => element.Status == TaskStatus.RanToCompletion);
                    var faultedShareScannerTasks = Array.FindAll(SharescannerTasks.ToArray(),
                        element => element.Status == TaskStatus.Faulted);
                    if ((completedShareScannerTasks.Length + faultedShareScannerTasks.Length) ==
                        SharescannerTasks.Count)
                    {
                        shareScannerTasksDone = true;
                        Config.Mq.Info("All ShareScanner tasks finished!");
                    }
                }
            }

            Config.Mq.Info("Finished!");
            Console.ResetColor();
            Environment.Exit(0);
            // This is the main execution thread.
        }

        // This method is called every minute
        private void StatusUpdate(object sender, ElapsedEventArgs e)
        {
            var totalShareFinderTasksCount = SharefinderTasks.Count;
            var totalShareScannerTasksCount = SharescannerTasks.Count;
            var completedShareFinderTasks = Array.FindAll(SharefinderTasks.ToArray(),
                element => element.Status == TaskStatus.RanToCompletion);
            var completedShareFinderTasksCount = completedShareFinderTasks.Length;

            var completedShareScannerTasks = Array.FindAll(SharescannerTasks.ToArray(),
                element => element.Status == TaskStatus.RanToCompletion);
            var completedShareScannerTasksCount = completedShareScannerTasks.Length;

            var updateText = new StringBuilder("Status Update: \n");

            updateText.Append("Sharescanner Tasks Completed: " + completedShareScannerTasksCount + "\n");
            updateText.Append("Sharescanner Tasks Remaining: " +
                              (totalShareScannerTasksCount - completedShareScannerTasksCount) + "\n");
            updateText.Append("Sharefinder Tasks Completed: " + completedShareFinderTasksCount + "\n");
            updateText.Append("Sharefinder Tasks Remaining: " +
                              (totalShareFinderTasksCount - completedShareFinderTasksCount) + "\n");

            Config.Mq.Info(updateText.ToString());
        }
    }
}