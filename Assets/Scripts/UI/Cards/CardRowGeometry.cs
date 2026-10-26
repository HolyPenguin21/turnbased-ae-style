using System;

namespace Game.UI
{
    // Design-space coordinates shared by hand rendering and reorder hit testing.
    internal readonly struct CardRowGeometry
    {
        public readonly float Scale, Step;
        private readonly int count;
        private readonly float referenceHeight;
        public readonly float CenterOffsetY;

        private CardRowGeometry(int count, float scale, float step, float referenceHeight, float height)
        {
            this.count = count; Scale = scale; Step = step; this.referenceHeight = referenceHeight;
            CenterOffsetY = (referenceHeight * scale - height) * .5f;
        }

        public static CardRowGeometry Fit(int count, float preferredStep, float referenceWidth,
            float referenceHeight, float width, float height)
        {
            float widthRatio = Math.Max(0f, width) / Math.Max(.01f, referenceWidth);
            float heightRatio = Math.Max(0f, height) / Math.Max(.01f, referenceHeight);
            float scale = Math.Min(widthRatio, heightRatio);
            return new CardRowGeometry(Math.Max(1, count), scale,
                preferredStep * scale, referenceHeight, Math.Max(0f, height));
        }

        public float SlotX(float index) => (index - (count - 1) * .5f) * Step;
        public float IndexAt(float x) => Step > 0f ? (x - SlotX(0)) / Step : 0f;
        public float DecorationOffsetY(float authoredY) => CenterOffsetY + (authoredY - referenceHeight * .5f) * Scale;
    }
}
