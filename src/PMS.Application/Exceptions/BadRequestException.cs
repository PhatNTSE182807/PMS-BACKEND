namespace PMS.Application.Exceptions;

[Serializable]
public class BadRequestException(string message) : Exception(message);
