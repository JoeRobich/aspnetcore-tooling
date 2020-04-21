/* --------------------------------------------------------------------------------------------
 * Copyright (c) Microsoft Corporation. All rights reserved.
 * Licensed under the MIT License. See License.txt in the project root for license information.
 * -------------------------------------------------------------------------------------------- */

import * as vscode from 'vscode';
import { RazorLanguageServerClient } from './RazorLanguageServerClient';

export function listenToConfigurationChanges(
    languageServerClient: RazorLanguageServerClient): vscode.Disposable {
    return vscode.workspace.onDidChangeConfiguration(event => {
        if (event.affectsConfiguration('razor.trace')) {
            razorTraceConfigurationChangeHandler(languageServerClient);
        }
    });
}

function razorTraceConfigurationChangeHandler(languageServerClient: RazorLanguageServerClient): void {
    const promptText = 'Would you like to restart the Razor Language Server to enable the Razor trace configuration change?';
    const restartButtonText = 'Restart';

    // eslint-disable-next-line @typescript-eslint/no-floating-promises
    vscode.window.showInformationMessage(promptText, restartButtonText).then(async result => {
        if (result !== restartButtonText) {
            return;
        }

        await languageServerClient.stop();
        languageServerClient.updateTraceLevel();
        await languageServerClient.start();
    });
}
