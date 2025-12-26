namespace LapTrinhMang.Dtos;

public record CreateReservationDto(
    int TableNumber,
    DateTime StartTime,
    int Hours,
    string CustomerName,
    string Phone

);
