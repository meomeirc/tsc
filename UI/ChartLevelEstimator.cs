using System;

namespace TSC;

/// <summary>
/// Estimates a placeholder level from the six chart radar measurements.
/// One pooled stock-chart model is shared by every difficulty; the estimate depends
/// only on the measured chart, never on its difficulty label. Stock level 10 is a
/// lower-bound/censored label rather than an exact maximum, so predictions are not
/// capped at 10.
/// </summary>
internal static class ChartLevelEstimator
{
    // Bump this whenever the mapping changes so persisted estimates are recalculated.
    public const int ModelVersion = 4;

    // One difficulty-blind model fitted to all 912 stock Easy/Normal/Hard/Expert
    // charts. The six radar axes keep their existing normalization references;
    // only the level mapping is recalibrated. Stock level 10 is right-censored
    // (10 or higher), and all SC charts are excluded from the fit.
    //
    // Fit method: Gaussian right-censored regression on ln(1 + radar / 100),
    // with non-negative coefficients. The two zero coefficients are the fitted
    // boundary solution for FLICKS and TRAVEL, not manually selected exclusions.
    private const double Intercept = 0.297730176487025;
    private const double Notes = 7.146377211976049;
    private const double Chords = 0.905006046205730;
    private const double Slides = 3.128733675190748;
    private const double Flicks = 0.0;
    private const double Stream = 6.015444898221029;
    private const double Travel = 0.0;

    public static double? Estimate(ChartRadarResult radar)
    {
        ArgumentNullException.ThrowIfNull(radar);

        var features = new[]
        {
            radar.Notes.NormalizedValue,
            radar.Chords.NormalizedValue,
            radar.Slides.NormalizedValue,
            radar.Flicks.NormalizedValue,
            radar.Stream.NormalizedValue,
            radar.Travel.NormalizedValue
        };
        foreach (var feature in features)
            if (!double.IsFinite(feature) || feature < 0.0)
                return null;

        static double Transform(double normalizedValue)
            => Math.Log(1.0 + normalizedValue / 100.0);

        var estimate = Intercept
            + Notes * Transform(features[0])
            + Chords * Transform(features[1])
            + Slides * Transform(features[2])
            + Flicks * Transform(features[3])
            + Stream * Transform(features[4])
            + Travel * Transform(features[5]);

        if (!double.IsFinite(estimate))
            return null;

        // Preserve the continuous result. UI surfaces format it to one decimal
        // place; rounding here would erase small differences before display.
        // Do not clamp to level 10: stock labels at 10 mean "10 or higher".
        return Math.Max(estimate, 1.0);
    }
}
