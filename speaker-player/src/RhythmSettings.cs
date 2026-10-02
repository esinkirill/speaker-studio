namespace SpeakerPlayer
{
    public enum RhythmMode { Original, Quantize, Chop }

    public sealed class RhythmSettings
    {
        public RhythmMode Mode = RhythmMode.Original;
        public double SourceBpm = 0;
        public int PartsPerBeat = 4;
        public double PhaseMs = 0;
        public int SoundPercent = 90;
        public int GlideMs = 0;
        public int CurveResolutionMs = 10;

        public RhythmSettings Copy()
        {
            return new RhythmSettings {
                Mode = Mode, SourceBpm = SourceBpm, PartsPerBeat = PartsPerBeat,
                PhaseMs = PhaseMs, SoundPercent = SoundPercent,
                GlideMs = GlideMs, CurveResolutionMs = CurveResolutionMs
            };
        }
    }
}
