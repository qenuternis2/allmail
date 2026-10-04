namespace ProtonProfiles.Core.Model;

/// <summary>Per-profile opt-in exceptions; they remove only the named restriction of the selected policy.</summary>
[Flags]
public enum PrivacyException : long
{
    None=0, Graphics=1L<<0, CanvasReadback=1L<<1, WebAudio=1L<<2,
    SpeechSynthesis=1L<<3, LocalFonts=1L<<4, ServiceWorkers=1L<<5,
    SharedWorkers=1L<<6, StorageEstimate=1L<<7, MediaDevices=1L<<8,
    MediaCapabilities=1L<<9, WebCodecs=1L<<10, KeyboardLayout=1L<<11,
    Battery=1L<<12, Gamepads=1L<<13, Camera=1L<<14, Microphone=1L<<15
}
