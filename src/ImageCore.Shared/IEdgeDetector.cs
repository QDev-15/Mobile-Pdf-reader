namespace ImageCoreService;

/// <summary>
/// Result of paper-edge detection. <see cref="Quad"/> is in coordinates normalized to 0..1 of
/// the analyzed (upright) image. When nothing convincing was found, <see cref="Detected"/> is
/// false and the quad is the whole image with a small margin, so the caller always has a
/// usable starting point for the manual 4-point editor.
/// </summary>
public sealed record QuadDetection(Quad Quad, double Confidence, bool Detected);

public interface IEdgeDetector
{
    QuadDetection Detect(RgbImage image);
}
