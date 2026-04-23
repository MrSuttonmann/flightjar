using FlightJar.Persistence.Notifications;

namespace FlightJar.Notifications;

public interface INotifier
{
    NotificationChannelType Kind { get; }
    Task SendAsync(NotificationMessage msg, NotificationChannel channel, CancellationToken ct);
}
