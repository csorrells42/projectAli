Texture2D<float> CameraLuma : register(t0);
Texture2D<float2> CameraChroma : register(t1);
SamplerState CameraSampler : register(s0);

cbuffer Nv12PreviewSettings : register(b0)
{
    float ExposureOffset;
    float Contrast;
    float Saturation;
    float Warmth;
    float DenoiseAmount;
    float DenoiseEdgeThreshold;
    float TexelWidth;
    float TexelHeight;
    float SwapChromaChannels;
};

struct VertexOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

VertexOutput VSMain(uint vertexId : SV_VertexID)
{
    float2 positions[3] =
    {
        float2(-1.0, -1.0),
        float2(-1.0, 3.0),
        float2(3.0, -1.0)
    };
    float2 texCoords[3] =
    {
        float2(0.0, 1.0),
        float2(0.0, -1.0),
        float2(2.0, 1.0)
    };
    VertexOutput output;
    output.Position = float4(positions[vertexId], 0.0, 1.0);
    output.TexCoord = texCoords[vertexId];
    return output;
}

float NormalizeLuma(float rawY)
{
    return saturate((rawY - (16.0 / 255.0)) * (255.0 / 219.0));
}

float EdgeAwareWeight(float sampleY, float centerY)
{
    return saturate(1.0 - abs(sampleY - centerY) / max(DenoiseEdgeThreshold, 0.0001));
}

float SampleNormalizedLuma(float2 texCoord)
{
    return NormalizeLuma(CameraLuma.Sample(CameraSampler, texCoord));
}

float ApplyLumaDenoise(float centerY, float2 texCoord)
{
    if (DenoiseAmount <= 0.001)
    {
        return centerY;
    }
    float2 xOffset = float2(TexelWidth, 0.0);
    float2 yOffset = float2(0.0, TexelHeight);
    float leftY = SampleNormalizedLuma(texCoord - xOffset);
    float rightY = SampleNormalizedLuma(texCoord + xOffset);
    float upY = SampleNormalizedLuma(texCoord - yOffset);
    float downY = SampleNormalizedLuma(texCoord + yOffset);
    float centerWeight = 2.0;
    float leftWeight = EdgeAwareWeight(leftY, centerY);
    float rightWeight = EdgeAwareWeight(rightY, centerY);
    float upWeight = EdgeAwareWeight(upY, centerY);
    float downWeight = EdgeAwareWeight(downY, centerY);
    float totalWeight = centerWeight + leftWeight + rightWeight + upWeight + downWeight;
    float smoothedY = (
        centerY * centerWeight
        + leftY * leftWeight
        + rightY * rightWeight
        + upY * upWeight
        + downY * downWeight) / max(totalWeight, 0.0001);
    return lerp(centerY, smoothedY, DenoiseAmount);
}

float3 ApplyColorPolish(float3 rgb)
{
    float3 rgb255 = rgb * 255.0;
    rgb255.r = ((rgb255.r + ExposureOffset + Warmth - 128.0) * Contrast) + 128.0;
    rgb255.g = ((rgb255.g + ExposureOffset - 128.0) * Contrast) + 128.0;
    rgb255.b = ((rgb255.b + ExposureOffset - Warmth - 128.0) * Contrast) + 128.0;
    float luma = dot(rgb255, float3(0.2126, 0.7152, 0.0722));
    rgb255 = luma + (rgb255 - luma) * Saturation;
    return saturate(rgb255 / 255.0);
}

float4 PSMain(VertexOutput input) : SV_TARGET
{
    float y = NormalizeLuma(CameraLuma.Sample(CameraSampler, input.TexCoord));
    y = ApplyLumaDenoise(y, input.TexCoord);
    float2 uv = CameraChroma.Sample(CameraSampler, input.TexCoord) - float2(0.5, 0.5);
    uv = SwapChromaChannels > 0.5 ? uv.yx : uv;
    float3 rgb = float3(
        y + 1.5748 * uv.y,
        y - 0.1873 * uv.x - 0.4681 * uv.y,
        y + 1.8556 * uv.x);
    return float4(ApplyColorPolish(saturate(rgb)), 1.0);
}
