namespace Cosine.Audio.Dsp;

/// <summary>
/// Orthonormal DCT-II - the decorrelating step that turns a log-mel spectrum into cepstral
/// coefficients. Computed against a precomputed cosine table because the transform runs once
/// per frame with a fixed, small size.
/// </summary>
public sealed class Dct
{
    private readonly double[,] _basis;
    private readonly int _inputLength;
    private readonly int _outputLength;

    public Dct(int inputLength, int outputLength)
    {
        if (inputLength <= 0) throw new ArgumentOutOfRangeException(nameof(inputLength));
        if (outputLength <= 0 || outputLength > inputLength)
            throw new ArgumentOutOfRangeException(nameof(outputLength));

        _inputLength = inputLength;
        _outputLength = outputLength;
        _basis = new double[outputLength, inputLength];

        var norm0 = Math.Sqrt(1.0 / inputLength);
        var normK = Math.Sqrt(2.0 / inputLength);

        for (var k = 0; k < outputLength; k++)
        {
            var scale = k == 0 ? norm0 : normK;
            for (var n = 0; n < inputLength; n++)
                _basis[k, n] = scale * Math.Cos(Math.PI / inputLength * (n + 0.5) * k);
        }
    }

    public double[] Transform(ReadOnlySpan<double> input)
    {
        if (input.Length != _inputLength)
            throw new ArgumentException($"Expected {_inputLength} inputs, got {input.Length}.", nameof(input));

        var output = new double[_outputLength];
        for (var k = 0; k < _outputLength; k++)
        {
            double sum = 0;
            for (var n = 0; n < _inputLength; n++)
                sum += input[n] * _basis[k, n];
            output[k] = sum;
        }

        return output;
    }
}
