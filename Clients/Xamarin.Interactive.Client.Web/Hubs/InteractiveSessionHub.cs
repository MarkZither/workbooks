﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using Newtonsoft.Json;

using Xamarin.Interactive.Client.Web.WebAssembly;
using Xamarin.Interactive.CodeAnalysis;
using Xamarin.Interactive.CodeAnalysis.Evaluating;
using Xamarin.Interactive.CodeAnalysis.Events;
using Xamarin.Interactive.CodeAnalysis.Models;
using Xamarin.Interactive.NuGet;
using Xamarin.Interactive.Session;

namespace Xamarin.Interactive.Client.Web.Hubs
{
    /// <summary>
    /// SignalR proxy for InteractiveSession.
    /// </summary>
    /// <remarks>
    /// Remember that SignalR Hub has the same lifecycle semantics as MVC
    /// controllers: a new instance will be created for each hub method
    /// invocation (including OnConnectedAsync and OnDisconnectedAsync).
    ///
    /// TL;DR: Do not store state in this class!
    /// </remarks>
    sealed class InteractiveSessionHub : Hub
    {
        readonly IMemoryCache memoryCache;
        readonly IHostingEnvironment hostingEnvironment;
        readonly ILogger<InteractiveSessionHub> logger;
        readonly ReferenceWhitelist referenceWhitelist;

        public InteractiveSessionHub (
            IMemoryCache memoryCache,
            IHostingEnvironment hostingEnvironment,
            ILogger<InteractiveSessionHub> logger,
            ReferenceWhitelist referenceWhitelist)
        {
            this.memoryCache = memoryCache;
            this.hostingEnvironment = hostingEnvironment;
            this.logger = logger;
            this.referenceWhitelist = referenceWhitelist;
        }

        public override Task OnConnectedAsync ()
        {
            memoryCache.GetOrCreate (
                Context.ConnectionId,
                e => new InteractiveSession ());

            return Task.CompletedTask;
        }

        public override Task OnDisconnectedAsync (Exception exception)
        {
            if (memoryCache.TryGetValue (
                Context.ConnectionId,
                out InteractiveSession session))
                session.Dispose ();

            memoryCache.Remove (Context.ConnectionId);

            return Task.CompletedTask;
        }

        InteractiveSession GetSession ()
            => memoryCache.Get<InteractiveSession> (Context.ConnectionId);

        public IEnumerable<WorkbookAppInstallation> GetAvailableWorkbookTargets ()
        {
            // If running in Azure, restrict to just the WASM workbook app
            if (Environment.GetEnvironmentVariable ("WEBSITE_INSTANCE_ID") != null)
                return WorkbookAppInstallation.All.Where (app => app.Id == "webassembly-monowebassembly");
            else
                return WorkbookAppInstallation.All;
        }

        public ChannelReader<InteractiveSessionEvent> ObserveSessionEvents ()
        {
            var channel = Channel.CreateUnbounded<InteractiveSessionEvent> ();

            void OnNext (InteractiveSessionEvent evnt)
            {
                if (hostingEnvironment.IsDevelopment ()) {
                    var settings = new Serialization.ExternalInteractiveJsonSerializerSettings ();
                    logger.LogDebug (
                        "posting session event: {0}",
                        JsonConvert.SerializeObject (evnt, settings));
                }

                if (evnt.Data is Compilation compilation)
                    compilation.References.ForEach (assemblyDefinition => {
                        if (assemblyDefinition.Content.Location.Exists)
                            referenceWhitelist.Add (assemblyDefinition.Content.Location);
                    });

                channel.Writer.TryWrite (evnt);
            }

            var subscription = GetSession ().Events.Subscribe (new Observer<InteractiveSessionEvent> (
                OnNext,
                error => channel.Writer.TryComplete (error),
                () => channel.Writer.TryComplete ()));

            var abortRegistration = Context.ConnectionAborted.Register (() => channel.Writer.TryComplete ());

            channel.Reader.Completion.ContinueWith (task => {
                subscription.Dispose ();
                abortRegistration.Dispose ();
            });

            return channel.Reader;
        }

        public Task InitializeSession (InteractiveSessionDescription sessionDescription)
        {
            referenceWhitelist.Clear ();
            return GetSession ().InitializeAsync (
                sessionDescription,
                Context.ConnectionAborted);
        }

        public Task<CodeCellId> InsertCodeCell (
            string initialBuffer,
            string relativeToCodeCellId,
            bool insertBefore)
            => GetSession ().EvaluationService.InsertCodeCellAsync (
                initialBuffer,
                relativeToCodeCellId,
                insertBefore,
                Context.ConnectionAborted);

        public Task<CodeCellUpdatedEvent> UpdateCodeCell (
            string codeCellId,
            string updatedBuffer)
            => GetSession ().EvaluationService.UpdateCodeCellAsync (
                codeCellId,
                updatedBuffer,
                Context.ConnectionAborted);

        public Task Evaluate (string targetCodeCellId, bool evaluateAll)
            => GetSession ().EvaluationService.EvaluateAsync (
                targetCodeCellId,
                evaluateAll,
                Context.ConnectionAborted);

        public void NotifyEvaluationComplete (string targetCodeCellId, EvaluationStatus status)
            => GetSession ().EvaluationService.NotifyEvaluationComplete (
                targetCodeCellId,
                status);

        public Task<Hover> GetHover (
            string codeCellId,
            Position position)
            => GetSession ().WorkspaceService.GetHoverAsync (
                codeCellId,
                position,
                Context.ConnectionAborted);

        public async Task<IEnumerable<CompletionItem>> GetCompletions (
            CodeCellId codeCellId,
            Position position)
        {
            var cancellationToken = Context.ConnectionAborted;

            try {
                return await GetSession ().WorkspaceService.GetCompletionsAsync (
                    codeCellId,
                    position,
                    cancellationToken);
            } catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) {
                return Array.Empty<CompletionItem> ();
            }
        }

        public Task<SignatureHelp> GetSignatureHelp (
            CodeCellId codeCellId,
            Position position)
            => GetSession ().WorkspaceService.GetSignatureHelpAsync (
                codeCellId,
                position,
                Context.ConnectionAborted);

        public async Task<IReadOnlyList<InteractivePackageDescription>> InstallPackages (
            IReadOnlyList<InteractivePackageDescription> packages)
        {
            var packageManagerService = GetSession ().PackageManagerService;
            await packageManagerService.InstallAsync (
                packages,
                Context.ConnectionAborted);
            return packageManagerService.GetInstalledPackages ();
        }

        public async Task<IReadOnlyList<InteractivePackageDescription>> RestorePackages (
            IReadOnlyList<InteractivePackageDescription> packages)
        {
            var packageManagerService = GetSession ().PackageManagerService;
            await packageManagerService.RestoreAsync (
                packages,
                Context.ConnectionAborted);
            return packageManagerService.GetInstalledPackages ();
        }
    }
}