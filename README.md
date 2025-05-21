# Gemini Voice Assistant (Czech)

## Description

The Gemini Voice Assistant is a .NET 8 console application that demonstrates a voice-controlled assistant experience. It utilizes Google Cloud Speech-to-Text for transcribing user speech, Google's Gemini Pro model (via the Vertex AI API) for generating intelligent responses, and Google Cloud Text-to-Speech for vocalizing the AI's answers. The application is configured for interaction in the Czech language and supports "barge-in" functionality, allowing the user to interrupt the AI while it's speaking.

## Features

*   **Voice-based interaction in Czech**: Speak to the assistant and receive spoken responses in Czech.
*   **Real-time Speech-to-Text**: Captures audio input and transcribes it to text using Google Cloud Speech-to-Text.
*   **Integration with Google Gemini API**: Sends transcribed text to a Gemini model (e.g., `gemini-1.5-flash-001`) via Vertex AI for response generation.
*   **Real-time Text-to-Speech**: Synthesizes text responses from Gemini into natural-sounding Czech speech using Google Cloud Text-to-Speech.
*   **Barge-in Capability**: Users can interrupt the AI while it's speaking by simply starting to speak again. The AI will stop, and the new user input will be processed.
*   **Configuration via `appsettings.json`**: Key settings like Project ID, region, model ID, and language preferences are managed through an external configuration file.

## Prerequisites

*   **.NET 8 SDK**: Download and install from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0).
*   **A Google Cloud Platform (GCP) Project**: If you don't have one, create a new project at [console.cloud.google.com](https://console.cloud.google.com/).
*   **Enabled GCP APIs**: Ensure the following APIs are enabled in your GCP project:
    *   Cloud Speech-to-Text API
    *   Cloud Text-to-Speech API
    *   Vertex AI API
    You can enable them from the "APIs & Services" > "Library" section of the GCP Console.
*   **A configured microphone and speakers**: Necessary for audio input and output.

## Setup Instructions

### 1. Clone Repository

Clone this repository to your local machine:

```bash
git clone <repository-url>
```

(Replace `<repository-url>` with the actual URL of this repository). Alternatively, you can download the source code as a ZIP file and extract it.

### 2. Google Cloud Authentication

This application uses Google Cloud client libraries that require Application Default Credentials (ADC) for authentication. The recommended way to provide these credentials is by using a service account key.

*   **Create a Service Account**:
    1.  In the GCP Console, navigate to "IAM & Admin" > "Service Accounts".
    2.  Select your project.
    3.  Click "+ CREATE SERVICE ACCOUNT".
    4.  Enter a name (e.g., "gemini-voice-assistant-sa") and an optional description.
    5.  Click "CREATE AND CONTINUE".
    6.  **Grant Roles**: Assign the following roles to the service account to ensure it has the necessary permissions:
        *   `Vertex AI User` (for Gemini API access)
        *   `Cloud Speech User` (for Speech-to-Text API)
        *   `Cloud Text-to-Speech User` (for Text-to-Speech API)
        *   You might also consider `Service Account User` if needed in specific contexts, but the above are primary.
    7.  Click "CONTINUE", then "DONE".
*   **Download JSON Key**:
    1.  Find the newly created service account in the list.
    2.  Click the three dots (Actions) next to it and select "Manage keys".
    3.  Click "ADD KEY" > "Create new key".
    4.  Select "JSON" as the key type and click "CREATE".
    5.  A JSON file will be downloaded to your computer. **Keep this file secure, as it grants access to your GCP resources.**

*   **Set Environment Variable**:
    Set the `GOOGLE_APPLICATION_CREDENTIALS` environment variable to the **absolute path** of the downloaded JSON key file.

    *   **Windows (Command Prompt)**:
        ```cmd
        set GOOGLE_APPLICATION_CREDENTIALS="C:\path\to\your\keyfile.json"
        ```
        (For PowerShell, use `$env:GOOGLE_APPLICATION_CREDENTIALS="C:\path\to\your\keyfile.json"`)
    *   **Linux / macOS (Bash)**:
        ```bash
        export GOOGLE_APPLICATION_CREDENTIALS="/path/to/your/keyfile.json"
        ```
        (Add this line to your `.bashrc` or `.zshrc` for persistence across sessions).

    **Important**: Replace the example path with the actual absolute path to your downloaded JSON key file.

### 3. Configure `appsettings.json`

The application uses `GeminiVoiceAssistant/appsettings.json` for its configuration.

*   Navigate to the `GeminiVoiceAssistant` directory within the cloned repository.
*   Open `appsettings.json` in a text editor.
*   **Crucial**: Modify the `GoogleCloud.ProjectId` value from the default `"your-project-id"` to your actual GCP Project ID.

    ```json
    {
      "GoogleCloud": {
        "ProjectId": "your-actual-gcp-project-id", // <--- CHANGE THIS
        "Region": "us-central1"
      },
      // ... other settings
    }
    ```

*   Other settings like `Region`, `Gemini.ModelId`, `SpeechToText.LanguageCode`, and `TextToSpeech` voice parameters are pre-configured for Czech (`cs-CZ`) and a common Gemini model (`gemini-1.5-flash-001`). You can review and adjust these if needed, but the defaults should work for the intended Czech language interaction.

### 4. Build the Application

*   Open a terminal or command prompt.
*   Navigate to the root directory of the project (e.g., `cd path/to/GeminiVoiceAssistant`).

    ```bash
    dotnet build
    ```

### 5. Run the Application

Ensure you are still in the `GeminiVoiceAssistant` project directory in your terminal.

```bash
dotnet run
```

## How to Use

1.  Once the application starts, it will display "Listening for your command...".
2.  Speak clearly into your microphone in Czech. Your speech will be transcribed in real-time (interim results might be shown).
3.  After you finish speaking, the final transcription will be sent to the Gemini model.
4.  The AI's response will then be spoken back to you using Text-to-Speech.
5.  **Barge-in**: If you want to interrupt the AI while it's speaking, simply start speaking again. The AI's speech will stop, and your new query will be captured and processed.

## Troubleshooting

*   **No audio input / "No audio recording devices found" / "No audio data captured"**:
    *   Ensure your microphone is properly connected and configured as the default recording device in your operating system.
    *   Check application permissions for microphone access if your OS restricts it.
    *   Run `AudioInput.ListAudioDevices()` (you can uncomment this in `Program.cs` temporarily) to see if NAudio detects your microphone.
*   **Authentication errors (e.g., "PermissionDenied", "Credentials not found")**:
    *   Verify that the `GOOGLE_APPLICATION_CREDENTIALS` environment variable is correctly set to the **absolute path** of your service account JSON key file.
    *   Ensure the service account associated with the key has the necessary IAM roles: "Vertex AI User", "Cloud Speech User", and "Cloud Text-to-Speech User".
    *   Make sure you've restarted your terminal/IDE after setting the environment variable for it to take effect.
*   **API Disabled errors (e.g., "API ... has not been used in project ... before or it is disabled.")**:
    *   Go to the Google Cloud Console and ensure that "Cloud Speech-to-Text API", "Cloud Text-to-Speech API", and "Vertex AI API" are enabled for your project.
*   **Incorrect Project ID / "Could not find the project"**:
    *   Double-check that `GoogleCloud.ProjectId` in `appsettings.json` exactly matches your GCP Project ID.
*   **Errors related to NAudio (e.g., "NoDriver calling waveOutOpen")**:
    *   Ensure you have speakers or an audio output device configured correctly in your operating system.

## Project Structure (Brief)

*   `Program.cs`: Contains the main application loop, orchestrates interactions between different components, and handles configuration loading.
*   `AudioInput.cs`: Manages audio capture from the microphone using NAudio and streaming speech recognition via Google Cloud Speech-to-Text.
*   `AudioOutput.cs`: Handles text-to-speech synthesis using Google Cloud Text-to-Speech and plays back the audio using NAudio. Implements cancellation for barge-in.
*   `GeminiClient.cs`: Interacts with the Google Gemini API (via Vertex AI) to send user queries and receive generated content.
*   `AppSettings.cs`: Defines C# classes that map to the structure of `appsettings.json` for strongly-typed configuration.
*   `appsettings.json`: External JSON file for application configuration (Project ID, API settings, language preferences, etc.).

---
This README provides a comprehensive guide for users to set up and run the Gemini Voice Assistant.The `README.md` file has been updated with the comprehensive information as requested. It now includes sections for Description, Features, Prerequisites, Setup Instructions, How to Use, Troubleshooting, and a brief Project Structure. All formatting is done using Markdown.
