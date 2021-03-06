﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.VisualStudio.LanguageServerClient.Razor.HtmlCSharp
{
    [Shared]
    [ExportLspMethod(Methods.TextDocumentDefinitionName)]
    internal class GoToDefinitionHandler : IRequestHandler<TextDocumentPositionParams, Location[]>
    {
        private readonly LSPRequestInvoker _requestInvoker;
        private readonly LSPDocumentManager _documentManager;
        private readonly LSPProjectionProvider _projectionProvider;
        private readonly LSPDocumentMappingProvider _documentMappingProvider;

        [ImportingConstructor]
        public GoToDefinitionHandler(
            LSPRequestInvoker requestInvoker,
            LSPDocumentManager documentManager,
            LSPProjectionProvider projectionProvider,
            LSPDocumentMappingProvider documentMappingProvider)
        {
            if (requestInvoker is null)
            {
                throw new ArgumentNullException(nameof(requestInvoker));
            }

            if (documentManager is null)
            {
                throw new ArgumentNullException(nameof(documentManager));
            }

            if (projectionProvider is null)
            {
                throw new ArgumentNullException(nameof(projectionProvider));
            }

            if (documentMappingProvider is null)
            {
                throw new ArgumentNullException(nameof(documentMappingProvider));
            }

            _requestInvoker = requestInvoker;
            _documentManager = documentManager;
            _projectionProvider = projectionProvider;
            _documentMappingProvider = documentMappingProvider;
        }

        public async Task<Location[]> HandleRequestAsync(TextDocumentPositionParams request, ClientCapabilities clientCapabilities, CancellationToken cancellationToken)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (clientCapabilities is null)
            {
                throw new ArgumentNullException(nameof(clientCapabilities));
            }

            if (!_documentManager.TryGetDocument(request.TextDocument.Uri, out var documentSnapshot))
            {
                return null;
            }

            var projectionResult = await _projectionProvider.GetProjectionAsync(documentSnapshot, request.Position, cancellationToken).ConfigureAwait(false);
            if (projectionResult == null || projectionResult.LanguageKind != RazorLanguageKind.CSharp)
            {
                return null;
            }

            var serverKind = LanguageServerKind.CSharp;
            var textDocumentPositionParams = new TextDocumentPositionParams()
            {
                Position = projectionResult.Position,
                TextDocument = new TextDocumentIdentifier()
                {
                    Uri = projectionResult.Uri
                }
            };

            var result = await _requestInvoker.ReinvokeRequestOnServerAsync<TextDocumentPositionParams, Location[]>(
                Methods.TextDocumentDefinitionName,
                serverKind,
                textDocumentPositionParams,
                cancellationToken).ConfigureAwait(false);

            if (result == null || result.Length == 0)
            {
                return result;
            }

            var remappedLocations = new List<Location>();

            foreach (var location in result)
            {
                if (!RazorLSPConventions.IsRazorCSharpFile(location.Uri))
                {
                    // This location doesn't point to a virtual cs file. No need to remap.
                    remappedLocations.Add(location);
                    continue;
                }

                var razorDocumentUri = RazorLSPConventions.GetRazorDocumentUri(location.Uri);
                var mappingResult = await _documentMappingProvider.MapToDocumentRangeAsync(
                    projectionResult.LanguageKind,
                    razorDocumentUri,
                    location.Range,
                    cancellationToken).ConfigureAwait(false);

                if (mappingResult == null || mappingResult.HostDocumentVersion != documentSnapshot.Version)
                {
                    // Couldn't remap the location or the document changed in the meantime. Discard this location.
                    continue;
                }
                
                var remappedLocation = new Location()
                {
                    Uri = razorDocumentUri,
                    Range = mappingResult.Range
                };

                remappedLocations.Add(remappedLocation);
            }

            return remappedLocations.ToArray();
        }
    }
}
