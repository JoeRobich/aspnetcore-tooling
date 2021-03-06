﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Composition;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.LanguageServer.Common;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.VisualStudio.LanguageServerClient.Razor.HtmlCSharp
{
    [Shared]
    [Export(typeof(LSPDocumentMappingProvider))]
    internal class DefaultLSPDocumentMappingProvider : LSPDocumentMappingProvider
    {
        private static readonly TextEdit[] EmptyEdits = Array.Empty<TextEdit>();

        private readonly LSPRequestInvoker _requestInvoker;
        private readonly LSPDocumentManager _documentManager;

        [ImportingConstructor]
        public DefaultLSPDocumentMappingProvider(LSPRequestInvoker requestInvoker, LSPDocumentManager documentManager)
        {
            if (requestInvoker is null)
            {
                throw new ArgumentNullException(nameof(requestInvoker));
            }

            if (documentManager is null)
            {
                throw new ArgumentNullException(nameof(documentManager));
            }

            _requestInvoker = requestInvoker;
            _documentManager = documentManager;
        }

        public async override Task<RazorMapToDocumentRangeResponse> MapToDocumentRangeAsync(RazorLanguageKind languageKind, Uri razorDocumentUri, Range projectedRange, CancellationToken cancellationToken)
        {
            if (razorDocumentUri is null)
            {
                throw new ArgumentNullException(nameof(razorDocumentUri));
            }

            if (projectedRange is null)
            {
                throw new ArgumentNullException(nameof(projectedRange));
            }

            var mapToDocumentRangeParams = new RazorMapToDocumentRangeParams()
            {
                Kind = languageKind,
                RazorDocumentUri = razorDocumentUri,
                ProjectedRange = new Range()
                {
                    Start = new Position(projectedRange.Start.Line, projectedRange.Start.Character),
                    End = new Position(projectedRange.End.Line, projectedRange.End.Character)
                }
            };

            var documentMappingResponse = await _requestInvoker.CustomRequestServerAsync<RazorMapToDocumentRangeParams, RazorMapToDocumentRangeResponse>(
                LanguageServerConstants.RazorMapToDocumentRangeEndpoint,
                LanguageServerKind.Razor,
                mapToDocumentRangeParams,
                cancellationToken).ConfigureAwait(false);

            return documentMappingResponse;
        }

        public async override Task<WorkspaceEdit> RemapWorkspaceEditAsync(WorkspaceEdit workspaceEdit, CancellationToken cancellationToken)
        {
            if (workspaceEdit == null)
            {
                return workspaceEdit;
            }
            else if (workspaceEdit.DocumentChanges != null)
            {
                // The LSP spec says, we should prefer `DocumentChanges` property over `Changes` if available.
                var remappedEdits = await RemapVersionedDocumentEditsAsync(workspaceEdit.DocumentChanges, cancellationToken).ConfigureAwait(false);
                return new WorkspaceEdit()
                {
                    DocumentChanges = remappedEdits
                };
            }
            else
            {
                var remappedEdits = await RemapDocumentEditsAsync(workspaceEdit.Changes, cancellationToken).ConfigureAwait(false);
                return new WorkspaceEdit()
                {
                    Changes = remappedEdits
                };
            }
        }

        private async Task<TextDocumentEdit[]> RemapVersionedDocumentEditsAsync(TextDocumentEdit[] documentEdits, CancellationToken cancellationToken)
        {
            var remappedDocumentEdits = new List<TextDocumentEdit>();
            foreach (var entry in documentEdits)
            {
                var uri = entry.TextDocument.Uri;
                if (!CanRemap(uri))
                {
                    // This location doesn't point to a background razor file. No need to remap.
                    remappedDocumentEdits.Add(entry);

                    continue;
                }

                var edits = entry.Edits;
                var (documentSnapshot, remappedEdits) = await RemapTextEditsAsync(uri, edits, cancellationToken).ConfigureAwait(false);
                if (documentSnapshot == null)
                {
                    // Couldn't find the document. Ignore this edit.
                    continue;
                }

                remappedDocumentEdits.Add(new TextDocumentEdit()
                {
                    TextDocument = new VersionedTextDocumentIdentifier()
                    {
                        Uri = documentSnapshot.Uri,
                        Version = documentSnapshot.Version
                    },
                    Edits = remappedEdits
                });
            }

            return remappedDocumentEdits.ToArray();
        }

        private async Task<Dictionary<string, TextEdit[]>> RemapDocumentEditsAsync(Dictionary<string, TextEdit[]> changes, CancellationToken cancellationToken)
        {
            var remappedChanges = new Dictionary<string, TextEdit[]>();
            foreach (var entry in changes)
            {
                var uri = new Uri(entry.Key);
                var edits = entry.Value;

                if (!CanRemap(uri))
                {
                    // This location doesn't point to a background razor file. No need to remap.
                    remappedChanges[entry.Key] = entry.Value;
                    continue;
                }

                var (documentSnapshot, remappedEdits) = await RemapTextEditsAsync(uri, edits, cancellationToken).ConfigureAwait(false);
                if (documentSnapshot == null)
                {
                    // Couldn't find the document. Ignore this edit.
                    continue;
                }

                remappedChanges[documentSnapshot.Uri.AbsoluteUri] = remappedEdits;
            }

            return remappedChanges;
        }

        private async Task<(LSPDocumentSnapshot, TextEdit[])> RemapTextEditsAsync(Uri uri, TextEdit[] edits, CancellationToken cancellationToken)
        {
            var languageKind = RazorLanguageKind.Razor;
            if (RazorLSPConventions.IsRazorCSharpFile(uri))
            {
                languageKind = RazorLanguageKind.CSharp;
            }
            else if (RazorLSPConventions.IsRazorHtmlFile(uri))
            {
                languageKind = RazorLanguageKind.Html;
            }
            else
            {
                Debug.Fail("This method should only be called for Razor background files.");
            }

            var razorDocumentUri = RazorLSPConventions.GetRazorDocumentUri(uri);
            if (!_documentManager.TryGetDocument(razorDocumentUri, out var documentSnapshot))
            {
                return (null, EmptyEdits);
            }

            var remappedEdits = new List<TextEdit>();
            foreach (var edit in edits)
            {
                var mappingResult = await MapToDocumentRangeAsync(
                languageKind,
                razorDocumentUri,
                edit.Range,
                cancellationToken).ConfigureAwait(false);

                if (mappingResult == null || mappingResult.HostDocumentVersion != documentSnapshot.Version)
                {
                    // Couldn't remap the location or the document changed in the meantime. Discard this location.
                    continue;
                }

                var remappedEdit = new TextEdit()
                {
                    Range = mappingResult.Range,
                    NewText = edit.NewText
                };

                remappedEdits.Add(remappedEdit);
            }

            return (documentSnapshot, remappedEdits.ToArray());
        }

        private static bool CanRemap(Uri uri)
        {
            return RazorLSPConventions.IsRazorCSharpFile(uri) || RazorLSPConventions.IsRazorHtmlFile(uri);
        }
    }
}
