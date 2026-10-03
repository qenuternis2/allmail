// Observes native entry points without enumerating voices, speaking, or replacing APIs.
function collectSpeechObservation(target = globalThis) {
  try {
    return {synthesisAvailable:typeof target.speechSynthesis !== 'undefined',
      synthesisConstructorAvailable:typeof target.SpeechSynthesis !== 'undefined',
      utteranceConstructorAvailable:typeof target.SpeechSynthesisUtterance !== 'undefined',
      voiceConstructorAvailable:typeof target.SpeechSynthesisVoice !== 'undefined'};
  } catch { return {status:'NotPerformed'}; }
}
