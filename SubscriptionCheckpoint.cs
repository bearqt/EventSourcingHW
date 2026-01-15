using System.ComponentModel.DataAnnotations;

public class SubscriptionCheckpoint
{
    [Key]
    public string SubscriptionId { get; set; } = string.Empty;
    public long Position { get; set; }
}