using TwinkleTray.Core;

internal static class WheelDeltaTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Wheel half-detents accumulate in either direction", () =>
        {
            var positive = new WheelDeltaAccumulator();
            Equal(0, positive.Add(60)); Equal(1, positive.Add(60)); Equal(0, positive.Add(0));
            var negative = new WheelDeltaAccumulator();
            Equal(0, negative.Add(-60)); Equal(-1, negative.Add(-60)); Equal(0, negative.Add(0));
        }),
        ("Wheel one-unit packets emit once per complete detent without overshoot", () =>
        {
            foreach (int direction in new[] { 1, -1 })
            {
                var wheel = new WheelDeltaAccumulator();
                for (int revolution = 0; revolution < 3; revolution++)
                {
                    for (int packet = 0; packet < 119; packet++) Equal(0, wheel.Add(direction));
                    Equal(direction, wheel.Add(direction));
                }
            }
        }),
        ("Wheel packets can emit several detents and retain their fractional tail", () =>
        {
            var wheel = new WheelDeltaAccumulator();
            Equal(2, wheel.Add(240)); Equal(-2, wheel.Add(-240));
            Equal(3, wheel.Add(390)); Equal(0, wheel.Add(89)); Equal(1, wheel.Add(1));
            Equal(-3, wheel.Add(-390)); Equal(0, wheel.Add(-89)); Equal(-1, wheel.Add(-1));
        }),
        ("Wheel opposite partial deltas cancel before producing a new detent", () =>
        {
            foreach (int direction in new[] { 1, -1 })
            {
                var wheel = new WheelDeltaAccumulator();
                Equal(0, wheel.Add(90 * direction)); Equal(0, wheel.Add(-60 * direction));
                Equal(0, wheel.Add(80 * direction)); Equal(direction, wheel.Add(10 * direction));
                Equal(0, wheel.Add(90 * direction)); Equal(-direction, wheel.Add(-210 * direction));
                Equal(0, wheel.Add(119 * direction)); Equal(direction, wheel.Add(direction));
            }
        }),
        ("Wheel reset discards pending input in either direction", () =>
        {
            var wheel = new WheelDeltaAccumulator();
            foreach (int direction in new[] { 1, -1 })
            {
                Equal(0, wheel.Add(119 * direction)); wheel.Reset();
                Equal(0, wheel.Add(direction)); Equal(direction, wheel.Add(119 * direction));
            }
            wheel.Reset(); wheel.Reset(); Equal(0, wheel.Add(0));
        }),
        ("Wheel accumulators do not transfer partial input between controls", () =>
        {
            var first = new WheelDeltaAccumulator(); var second = new WheelDeltaAccumulator();
            Equal(0, first.Add(60)); Equal(0, second.Add(60)); Equal(1, first.Add(60));
            first.Reset(); Equal(1, second.Add(60)); Equal(0, first.Add(60));
        }),
        ("Wheel integer-boundary packets preserve direction and residual units", () =>
        {
            var positive = new WheelDeltaAccumulator();
            Equal(0, positive.Add(119)); Equal(17_895_698, positive.Add(int.MaxValue));
            Equal(0, positive.Add(113)); Equal(1, positive.Add(1));
            var negative = new WheelDeltaAccumulator();
            Equal(0, negative.Add(-119)); Equal(-17_895_698, negative.Add(int.MinValue));
            Equal(0, negative.Add(-112)); Equal(-1, negative.Add(-1));
        }),
        ("Wheel repeated extreme packets cancel without cumulative overflow", () =>
        {
            var wheel = new WheelDeltaAccumulator();
            long emitted = 0;
            for (int iteration = 0; iteration < 1000; iteration++)
            {
                emitted += wheel.Add(int.MaxValue);
                emitted += wheel.Add(int.MinValue);
                emitted += wheel.Add(1);
            }
            Equal(0L, emitted); Equal(0, wheel.Add(119)); Equal(1, wheel.Add(1));
        }),
    ];

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
