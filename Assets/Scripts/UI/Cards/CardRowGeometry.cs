using System;

namespace Game.UI
{
    // Design-space coordinates shared by hand rendering and reorder hit testing.
    internal readonly struct CardRowGeometry
    {
        public readonly float Scale, Step;
        private readonly int count;
        private readonly float referenceHeight;

        private CardRowGeometry(int count, float scale, float step, float referenceHeight)
        { this.count = count; Scale = scale; Step = step; this.referenceHeight = referenceHeight; }

        public static CardRowGeometry Fit(int count, float preferredStep, float referenceWidth,
            float referenceHeight, float width, float height)
        {
            float widthRatio = Math.Max(0f, width) / Math.Max(.01f, referenceWidth);
            float heightRatio = Math.Max(0f, height) / Math.Max(.01f, referenceHeight);
            return new CardRowGeometry(Math.Max(1, count), Math.Min(widthRatio, heightRatio),
                preferredStep * widthRatio, referenceHeight);
        }

        public float SlotX(int index) => (index - (count - 1) * .5f) * Step;
        public float IndexAt(float x) => Step > 0f ? (x - SlotX(0)) / Step : 0f;
        public float DecorationOffsetY(float authoredY) => (authoredY - referenceHeight * .5f) * Scale;
    }
}
