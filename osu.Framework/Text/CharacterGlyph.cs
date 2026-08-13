// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Runtime.CompilerServices;
using System.Text;
using osu.Framework.IO.Stores;

namespace osu.Framework.Text
{
    public sealed class CharacterGlyph : ICharacterGlyph
    {
        public float XOffset { get; }
        public float YOffset { get; }
        public float XAdvance { get; }
        public float Baseline { get; }
        public int Codepoint { get; }
        public char Character => Codepoint <= char.MaxValue ? (char)Codepoint : '\0';

        /// <summary>
        /// Whether this glyph already contains colour information and should not be tinted by the parent text colour.
        /// </summary>
        public bool HasColour { get; }

        private readonly IGlyphStore? containingStore;

        public CharacterGlyph(int codepoint, float xOffset, float yOffset, float xAdvance, float baseline, IGlyphStore? containingStore, bool hasColour = false)
        {
            this.containingStore = containingStore;

            if (!Rune.IsValid(codepoint))
                throw new System.ArgumentOutOfRangeException(nameof(codepoint), codepoint, "Must be a valid Unicode scalar value.");

            Codepoint = codepoint;
            XOffset = xOffset;
            YOffset = yOffset;
            XAdvance = xAdvance;
            Baseline = baseline;
            HasColour = hasColour;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetKerning<T>(T lastGlyph)
            where T : ICharacterGlyph
            => containingStore?.GetKerning(lastGlyph.Codepoint, Codepoint) ?? 0;
    }
}
