using System;

namespace UniGetUI.Avalonia.Views.Controls;

internal static class SmoothScrollPhysics
{
    internal const double DecayTime = 0.15;
    internal const double WheelDistance = 48.0;
    internal const double PrecisionTouchpadDistance = WheelDistance / 2.0;
    internal const double PrecisionTouchpadDecayTime = DecayTime * 2.0;
    private const double WheelVelocityImpulse = WheelDistance / DecayTime;
    private const double MaximumVelocity = 7200.0;

    internal static (double X, double Y) AddImpulse(
        double velocityX,
        double velocityY,
        double deltaX,
        double deltaY)
        => (
            AddAxisImpulse(velocityX, deltaX),
            AddAxisImpulse(velocityY, deltaY));

    internal static (double StepX, double StepY, double VelocityX, double VelocityY) Integrate(
        double velocityX,
        double velocityY,
        double elapsedSeconds)
        => IntegrateWithDecay(velocityX, velocityY, elapsedSeconds, DecayTime);

    internal static (double StepX, double StepY, double VelocityX, double VelocityY)
        IntegratePrecisionTouchpad(
            double velocityX,
            double velocityY,
            double elapsedSeconds)
        => IntegrateWithDecay(
            velocityX,
            velocityY,
            elapsedSeconds,
            PrecisionTouchpadDecayTime);

    private static (double StepX, double StepY, double VelocityX, double VelocityY) IntegrateWithDecay(
        double velocityX,
        double velocityY,
        double elapsedSeconds,
        double decayTime)
    {
        double decay = Math.Exp(-elapsedSeconds / decayTime);
        double distanceFactor = decayTime * (1.0 - decay);
        return (
            velocityX * distanceFactor,
            velocityY * distanceFactor,
            velocityX * decay,
            velocityY * decay);
    }

    private static double AddAxisImpulse(double velocity, double delta)
    {
        if (delta == 0) return velocity;
        if (velocity != 0 && Math.Sign(velocity) != Math.Sign(delta)) velocity = 0;
        return Math.Clamp(
            velocity + delta * WheelVelocityImpulse,
            -MaximumVelocity,
            MaximumVelocity);
    }
}
