using Microsoft.EntityFrameworkCore;

namespace Vfps.Data.Models;

public class TracksCreationAndUpdates
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }
}
