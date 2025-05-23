namespace GeminiVoiceAssistant
{
    public class GoogleCloudSettings
    {
        public string ProjectId { get; set; }
        public string Region { get; set; }
    }

    public class GeminiSettings
    {
        public string ModelId { get; set; }
        public string? SystemPrompt { get; set; }
    }

    public class SpeechToTextSettings
    {
        public string LanguageCode { get; set; }
    }

    public class TextToSpeechSettings
    {
        public string LanguageCode { get; set; }
        public string VoiceName { get; set; }
        public int? SampleRateHertz { get; set; } // Nullable because it might not always be set in config
    }

    public class AppSettings
    {
        public GoogleCloudSettings GoogleCloud { get; set; }
        public GeminiSettings Gemini { get; set; }
        public SpeechToTextSettings SpeechToText { get; set; }
        public TextToSpeechSettings TextToSpeech { get; set; }
    }
}
