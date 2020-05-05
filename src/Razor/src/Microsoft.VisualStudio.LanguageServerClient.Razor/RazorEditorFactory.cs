// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Package;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace Microsoft.VisualStudio.LanguageServerClient.Razor
{
    [Guid(EditorFactoryGuidString)]
    internal class RazorEditorFactory : EditorFactory, IVsEditorFactoryChooser
    {
        private static readonly Guid WTEEditorFactoryGuid = new Guid("40d31677-cbc0-4297-a9ef-89d907823a98");
        private static readonly Guid RazorEditorFactoryGuid = new Guid(EditorFactoryGuidString);

        private const string EditorFactoryGuidString = "3dfdce9e-1799-4372-8aa6-d8e65182fdfc";
        private readonly LSPEditorFeatureDetector _lspEditorFeatureDetector;
        private readonly DocumentTableEventSink _eventSink;
        private readonly RunningDocumentTable _documentTable;
        private readonly uint _adviseCookie;

        public RazorEditorFactory(AsyncPackage package) : base(package)
        {
            var componentModel = (IComponentModel)AsyncPackage.GetGlobalService(typeof(SComponentModel));
            _lspEditorFeatureDetector = componentModel.GetService<LSPEditorFeatureDetector>();
            _eventSink = new DocumentTableEventSink();
            _documentTable = new RunningDocumentTable();
            _adviseCookie = _documentTable.Advise(_eventSink);
        }

        public int ChooseEditorFactory(string moniker, IVsHierarchy hierarchy, uint itemid, IntPtr punkDocDataExisting, ref Guid currentView, out Guid resolvedEditorFactory, out Guid resolvedView)
        {
            resolvedView = currentView;

            if (!_lspEditorFeatureDetector.IsLSPEditorAvailable(moniker, hierarchy))
            {
                // Razor LSP is not enabled, allow another editor to handle this document
                resolvedEditorFactory = WTEEditorFactoryGuid;
                return VSConstants.S_OK;
            }

            resolvedEditorFactory = RazorEditorFactoryGuid;
            return VSConstants.S_OK;
        }

        private class DocumentTableEventSink : IVsRunningDocTableEvents
        {
            public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
            {
                if (ErrorHandler.Succeeded(pFrame.GetGuidProperty((int)__VSFPROPID.VSFPROPID_guidEditorType, out Guid editorFactoryGuid)) &&
                    (editorFactoryGuid == WTEEditorFactoryGuid))
                {
                    ErrorHandler.ThrowOnFailure(pFrame.SetGuidProperty((int)__VSFPROPID.VSFPROPID_guidEditorType, RazorEditorFactoryGuid));
                }
                return VSConstants.S_OK;
            }

            public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;

            public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;

            public int OnAfterSave(uint docCookie) => VSConstants.S_OK;

            public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;

            public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
        }

        public override int CreateEditorInstance(
            uint createDocFlags,
            string moniker,
            string physicalView,
            IVsHierarchy hierarchy,
            uint itemid,
            IntPtr existingDocData,
            out IntPtr docView,
            out IntPtr docData,
            out string editorCaption,
            out Guid cmdUI,
            out int cancelled)
        {
            if (!_lspEditorFeatureDetector.IsLSPEditorAvailable(moniker, hierarchy))
            {
                Debug.Fail("Razor's EditorFactory chooser should have delegated EditorFactory on feature detection fail.");

                docView = default;
                docData = default;
                editorCaption = null;
                cmdUI = default;
                cancelled = 0;

                // Razor LSP is not enabled, allow another editor to handle this document
                return VSConstants.VS_E_UNSUPPORTEDFORMAT;
            }

            var editorInstance = base.CreateEditorInstance(createDocFlags, moniker, physicalView, hierarchy, itemid, existingDocData, out docView, out docData, out editorCaption, out cmdUI, out cancelled);
            var textLines = (IVsTextLines)Marshal.GetObjectForIUnknown(docData);

            // Next, the editor typically resets the ContentType after TextBuffer creation. We need to let them know
            // to not update the content type because we'll be taking care of the ContentType changing lifecycle.
            var userData = textLines as IVsUserData;
            var hresult = userData.SetData(VSConstants.VsTextBufferUserDataGuid.VsBufferDetectLangSID_guid, false);

            ErrorHandler.ThrowOnFailure(hresult);

            return editorInstance;
        }
    }
}
