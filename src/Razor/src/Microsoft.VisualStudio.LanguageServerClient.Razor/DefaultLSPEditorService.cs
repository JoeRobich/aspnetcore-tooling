﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Composition;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Razor.LanguageServer.Common;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.VisualStudio.LanguageServerClient.Razor
{
    [Shared]
    [Export(typeof(LSPEditorService))]
    internal class DefaultLSPEditorService : LSPEditorService
    {
        private readonly JoinableTaskFactory _joinableTaskFactory;
        private readonly SVsServiceProvider _serviceProvider;
        private readonly ITextBufferUndoManagerProvider _undoManagerProvider;
        private readonly IAsyncCompletionBroker _completionBroker;
        private readonly IVsEditorAdaptersFactoryService _adaptersFactoryService;

        [ImportingConstructor]
        public DefaultLSPEditorService(
            JoinableTaskContext joinableTaskContext,
            SVsServiceProvider serviceProvider,
            ITextBufferUndoManagerProvider undoManagerProvider,
            IAsyncCompletionBroker completionBroker,
            IVsEditorAdaptersFactoryService adaptersFactoryService)
        {
            if (joinableTaskContext is null)
            {
                throw new ArgumentNullException(nameof(joinableTaskContext));
            }

            if (serviceProvider is null)
            {
                throw new ArgumentNullException(nameof(serviceProvider));
            }

            if (undoManagerProvider is null)
            {
                throw new ArgumentNullException(nameof(undoManagerProvider));
            }

            if (completionBroker is null)
            {
                throw new ArgumentNullException(nameof(completionBroker));
            }

            if (adaptersFactoryService is null)
            {
                throw new ArgumentNullException(nameof(adaptersFactoryService));
            }

            _joinableTaskFactory = joinableTaskContext.Factory;
            _serviceProvider = serviceProvider;
            _undoManagerProvider = undoManagerProvider;
            _completionBroker = completionBroker;
            _adaptersFactoryService = adaptersFactoryService;
        }

        public async override Task ApplyTextEditsAsync(
            Uri uri,
            ITextSnapshot snapshot,
            IEnumerable<TextEdit> textEdits)
        {
            if (uri is null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (textEdits is null)
            {
                throw new ArgumentNullException(nameof(textEdits));
            }

            await _joinableTaskFactory.SwitchToMainThreadAsync();

            var undoManager = _undoManagerProvider.GetTextBufferUndoManager(snapshot.TextBuffer);
            var undoHistory = undoManager.TextBufferUndoHistory;
            using var transaction = undoHistory.CreateTransaction("Apply Razor LSP text edits");

            try
            {
                ApplyTextEdits(textEdits, snapshot, snapshot.TextBuffer);

                var cursorPosition = ExtractCursorPlaceholder(snapshot.TextBuffer.CurrentSnapshot, textEdits);
                if (cursorPosition != null)
                {
                    // If we get here it means we are in the host client. Move the cursor.

                    var fullPath = GetLocalFilePath(uri);

                    VsShellUtilities.OpenDocument(_serviceProvider, fullPath, VSConstants.LOGVIEWID.TextView_guid, out _, out _, out var windowFrame);

                    if (windowFrame != null)
                    {
                        var vsTextView = GetActiveVsTextView(windowFrame);

                        // Since we are moving the cursor we should dismiss any existing completion sessions as that is no longer valid.
                        var textView = _adaptersFactoryService.GetWpfTextView(vsTextView);
                        var session = _completionBroker.GetSession(textView);
                        if (session != null && !session.IsDismissed)
                        {
                            session.Dismiss();
                        }

                        MoveCaretToPosition(vsTextView, cursorPosition);
                    }
                }
            }
            finally
            {
                transaction.Complete();
            }
        }

        // Internal for testing
        internal static Position ExtractCursorPlaceholder(ITextSnapshot snapshot, IEnumerable<TextEdit> originalEdits)
        {
            var earliestLine = snapshot.LineCount;
            var hasPlaceholder = false;
            foreach (var edit in originalEdits)
            {
                if (edit.NewText.Contains(LanguageServerConstants.CursorPlaceholderString))
                {
                    hasPlaceholder = true;
                }

                if (edit.Range.Start.Line < earliestLine)
                {
                    earliestLine = edit.Range.Start.Line;
                }
            }

            if (!hasPlaceholder)
            {
                return null;
            }

            Position cursorPosition = null;
            for (var i = earliestLine; i < snapshot.LineCount; i++)
            {
                var lineText = snapshot.GetLineFromLineNumber(i).GetText();
                var placeholderOffset = lineText.IndexOf(LanguageServerConstants.CursorPlaceholderString, StringComparison.Ordinal);
                if (placeholderOffset != -1)
                {
                    cursorPosition = new Position(i, placeholderOffset);
                    break;
                }
            }

            Debug.Assert(cursorPosition != null);

            // Now that we have obtained the cursor position, let's remove the placeholder.
            var newEdit = new TextEdit();
            newEdit.Range = new Range()
            {
                Start = cursorPosition,
                End = new Position(cursorPosition.Line, cursorPosition.Character + LanguageServerConstants.CursorPlaceholderString.Length)
            };
            newEdit.NewText = string.Empty;

            ApplyTextEdits(new[] { newEdit }, snapshot, snapshot.TextBuffer);

            return cursorPosition;
        }

        private static void ApplyTextEdits(IEnumerable<TextEdit> textEdits, ITextSnapshot snapshot, ITextBuffer textBuffer)
        {
            var vsTextEdit = textBuffer.CreateEdit();
            foreach (var textEdit in textEdits)
            {
                if (textEdit.Range.Start == textEdit.Range.End)
                {
                    var position = GetSnapshotPositionFromProtocolPosition(snapshot, textEdit.Range.Start);
                    if (position > -1)
                    {
                        var span = GetTranslatedSpan(position, 0, snapshot, vsTextEdit.Snapshot);
                        vsTextEdit.Insert(span.Start, textEdit.NewText);
                    }
                }
                else if (string.IsNullOrEmpty(textEdit.NewText))
                {
                    var startPosition = GetSnapshotPositionFromProtocolPosition(snapshot, textEdit.Range.Start);
                    var endPosition = GetSnapshotPositionFromProtocolPosition(snapshot, textEdit.Range.End);
                    var difference = endPosition - startPosition;
                    if (startPosition > -1 && endPosition > -1 && difference > 0)
                    {
                        var span = GetTranslatedSpan(startPosition, difference, snapshot, vsTextEdit.Snapshot);
                        vsTextEdit.Delete(span);
                    }
                }
                else
                {
                    var startPosition = GetSnapshotPositionFromProtocolPosition(snapshot, textEdit.Range.Start);
                    var endPosition = GetSnapshotPositionFromProtocolPosition(snapshot, textEdit.Range.End);
                    var difference = endPosition - startPosition;

                    if (startPosition > -1 && endPosition > -1 && difference > 0)
                    {
                        var span = GetTranslatedSpan(startPosition, difference, snapshot, vsTextEdit.Snapshot);
                        vsTextEdit.Replace(span, textEdit.NewText);
                    }
                }
            }

            vsTextEdit.Apply();
        }

        public override void MoveCaretToPosition(string fullPath, int absoluteIndex)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                throw new ArgumentNullException(nameof(fullPath));
            }

            VsShellUtilities.OpenDocument(_serviceProvider, fullPath, VSConstants.LOGVIEWID.TextView_guid, out _, out _, out var windowFrame);

            if (windowFrame != null)
            {
                var vsTextView = GetActiveVsTextView(windowFrame);

                // Since we are moving the cursor we should dismiss any existing completion sessions as that is no longer valid.
                var textView = _adaptersFactoryService.GetWpfTextView(vsTextView);
                var session = _completionBroker.GetSession(textView);
                if (session != null && !session.IsDismissed)
                {
                    session.Dismiss();
                }

                ErrorHandler.ThrowOnFailure(vsTextView.GetLineAndColumn(absoluteIndex, out var line, out var column));

                var position = new Position()
                {
                    Line = line,
                    Character = column
                };

                MoveCaretToPosition(vsTextView, position);
            }
        }

        private static void MoveCaretToPosition(IVsTextView textView, Position position, bool sendFocus = true)
        {
            textView.SetCaretPos(position.Line, position.Character);
            textView.EnsureSpanVisible(new TextSpan() { iStartIndex = position.Character, iStartLine = position.Line, iEndIndex = position.Character, iEndLine = position.Line });
            textView.CenterLines(position.Line, 1);
            if (sendFocus)
            {
                textView.SendExplicitFocus();
            }
        }

        private static SnapshotPoint GetSnapshotPositionFromProtocolPosition(ITextSnapshot textSnapshot, Position position)
        {
            var line = textSnapshot.GetLineFromLineNumber(position.Line);
            var snapshotPosition = line.Start + position.Character;

            return new SnapshotPoint(textSnapshot, snapshotPosition);
        }

        private static Span GetTranslatedSpan(int startPosition, int length, ITextSnapshot oldSnapshot, ITextSnapshot newSnapshot)
        {
            var span = new Span(startPosition, length);

            if (oldSnapshot.Version != newSnapshot.Version)
            {
                var snapshotSpan = new SnapshotSpan(oldSnapshot, span);
                var translatedSnapshotSpan = snapshotSpan.TranslateTo(newSnapshot, SpanTrackingMode.EdgeExclusive);
                span = translatedSnapshotSpan.Span;
            }

            return span;
        }

        private static string GetLocalFilePath(Uri documentUri)
        {
            Requires.Argument(documentUri.IsFile, nameof(documentUri), "There were no clients that can open the document.");

            // Note: this would remove the '/' from some Uri returned on some LSP providers
            var absolutePath = documentUri.LocalPath.TrimStart('/');
            var fullPath = Path.GetFullPath(absolutePath);

            return fullPath;
        }

        private static IVsTextView GetActiveVsTextView(IVsWindowFrame windowFrame)
        {
            Requires.NotNull(windowFrame, nameof(windowFrame));

            // We want to query for IVsCodeWindow here in order to get the correct text view in diff mode.
            // Using windowFrame.GetProperty() method gets us the difference viewer that's always tied to the right view, which won't work for inline diff.
            IntPtr ppCodeWindow;
            ErrorHandler.ThrowOnFailure(windowFrame.QueryViewInterface(typeof(IVsCodeWindow).GUID, out ppCodeWindow));

            var vsCodeWindow = Marshal.GetObjectForIUnknown(ppCodeWindow) as IVsCodeWindow;
            Marshal.Release(ppCodeWindow);

            if (vsCodeWindow != null)
            {
                // We want to call GetLastActiveView() to make sure we get the right text view to support inline diff.
                // Otherwise we might be stuck with the right view only for side by side diff.
                ErrorHandler.ThrowOnFailure(vsCodeWindow.GetLastActiveView(out IVsTextView textView));
                return textView;
            }

            return null;
        }
    }
}
