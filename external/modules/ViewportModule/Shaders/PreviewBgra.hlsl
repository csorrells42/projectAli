Texture2D<float4> CameraFrame : register(t0);
SamplerState CameraSampler : register(s0);

cbuffer ColorSettings : register(b0)
{
    float ExposureOffset;
    float Contrast;
    float Saturation;
    float Warmth;
    float DenoiseAmount;
    float DenoiseEdgeThreshold;
    float TexelWidth;
    float TexelHeight;
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

float Luma(float3 rgb)
{
    return dot(rgb, float3(0.2126, 0.7152, 0.0722));
}

float EdgeAwareWeight(float sampleY, float centerY)
{
    return saturate(1.0 - abs(sampleY - centerY) / max(DenoiseEdgeThreshold, 0.0001));
}

float3 ApplyBgraDenoise(float3 centerRgb, float2 texCoord)
{
    if (DenoiseAmount <= 0.001)
    {
        return centerRgb;
    }
    float2 xOffset = float2(TexelWidth, 0.0);
    float2 yOffset = float2(0.0, TexelHeight);
    float3 leftRgb = CameraFrame.Sample(CameraSampler, texCoord - xOffset).rgb;
    float3 rightRgb = CameraFrame.Sample(CameraSampler, texCoord + xOffset).rgb;
    float3 upRgb = CameraFrame.Sample(CameraSampler, texCoord - yOffset).rgb;
    float3 downRgb = CameraFrame.Sample(CameraSampler, texCoord + yOffset).rgb;
    float centerY = Luma(centerRgb);
    float centerWeight = 2.0;
    float leftWeight = EdgeAwareWeight(Luma(leftRgb), centerY);
    float rightWeight = EdgeAwareWeight(Luma(rightRgb), centerY);
    float upWeight = EdgeAwareWeight(Luma(upRgb), centerY);
    float downWeight = EdgeAwareWeight(Luma(downRgb), centerY);
    float totalWeight = centerWeight + leftWeight + rightWeight + upWeight + downWeight;
    float3 smoothedRgb = (
        centerRgb * centerWeight
        + leftRgb * leftWeight
        + rightRgb * rightWeight
        + upRgb * upWeight
        + downRgb * downWeight) / max(totalWeight, 0.0001);
    return lerp(centerRgb, smoothedRgb, DenoiseAmount);
}

float4 PSMain(VertexOutput input) : SV_TARGET
{
    float4 color = CameraFrame.Sample(CameraSampler, input.TexCoord);
    float3 denoised = ApplyBgraDenoise(color.rgb, input.TexCoord);
    return float4(ApplyColorPolish(denoised), 1.0);
}
