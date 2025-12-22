namespace LapTrinhMang.Models;

public class TableEntity
{
    public int Id { get; set; }
    public int Number { get; set; }
    public int Capacity { get; set; }
    public string Type { get; set; } = "Normal"; // Normal | VIP | VVIP

}
