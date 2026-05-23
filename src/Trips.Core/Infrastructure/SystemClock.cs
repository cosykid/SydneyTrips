using Trips.Core.Abstractions;

namespace Trips.Core.Infrastructure;

/// <summary>Production implementation of <see cref="IClock"/>; delegates to <see cref="DateTimeOffset.UtcNow"/>.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
