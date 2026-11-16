using System;

namespace TSC;

/// <summary>
/// Estimates a placeholder Song Select level from the six chart radar measurements.
/// The model was fitted from the stock chart pack's Easy/Normal/Hard/Expert charts
/// (228 charts per difficulty). SC charts were not used. Stock level 10 is treated
/// as a lower-bound/censored label rather than an exact maximum, so predictions
/// are deliberately not capped at 10.
/// </summary>
internal static class ChartLevelEstimator
{
    public const int ModelVersion = 1;

    private readonly record struct Model(
        double Intercept,
        double Notes,
        double Chords,
        double Slides,
        double Flicks,
        double Stream,
        double Travel);

    // Positive coefficients keep the estimate monotonic: increasing any radar
    // measurement cannot lower the predicted level. Each feature is transformed
    // with ln(1 + normalizedRadar / 100) to reduce the effect of extreme values
    // while still allowing the model to extrapolate above the stock level cap.
    private static readonly Model Easy = new(
        0.13374364185749993,
        7.598986500885317,
        0.16431991657054,
        3.959298359728462,
        0.0,
        5.009083485857133,
        0.24050485598993382);

    private static readonly Model Normal = new(
        0.8686294388729918,
        4.977856612420676,
        0.0,
        2.058837946743678,
        0.0,
        6.842674427935254,
        0.0);

    private static readonly Model Hard = new(
        2.0625869773057657,
        4.395081515741018,
        0.5442596360592317,
        1.0434213562206571,
        0.2822145103188899,
        5.874302601885639,
        0.0);

    private static readonly Model Expert = new(
        2.133579114730343,
        4.577021460857592,
        0.9795651286944509,
        2.0745579932375673,
        0.9839702356565755,
        5.708209403867383,
        0.0);

    public static int? Estimate(ChartRadarResult radar, string difficulty)
    {
        ArgumentNullException.ThrowIfNull(radar);

        Model? model = difficulty.Trim().ToLowerInvariant() switch
        {
            "ez" or "easy" => Easy,
            "nm" or "normal" => Normal,
            "hd" or "hard" => Hard,
            // The stock pack has no XT sample set. Use the closest available
            // Expert calibration rather than inventing an untrained fifth model.
            "ex" or "expert" or "xt" or "extra" => Expert,
            _ => null
        };
        if (!model.HasValue)
            return null;

        var value = model.Value;
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

        var estimate = value.Intercept
            + value.Notes * Transform(features[0])
            + value.Chords * Transform(features[1])
            + value.Slides * Transform(features[2])
            + value.Flicks * Transform(features[3])
            + value.Stream * Transform(features[4])
            + value.Travel * Transform(features[5]);

        if (!double.IsFinite(estimate))
            return null;

        // Only lower-bound the result. In particular, do not clamp to level 10:
        // stock labels at 10 were treated as "10 or higher" during training.
        var rounded = Math.Round(estimate, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(rounded, 1.0, int.MaxValue);
    }
}
