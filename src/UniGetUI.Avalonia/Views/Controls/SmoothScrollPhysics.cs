using System;

namespace UniGetUI.Avalonia.Views.Controls;

internal static class SmoothScrollPhysics
{
    internal const double DecayTime = 0.15;
    private const double WheelDistance = 48.0;
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
    {
        double decay = Math.Exp(-elapsedSeconds / DecayTime);
        double distanceFactor = DecayTime * (1.0 - decay);
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
