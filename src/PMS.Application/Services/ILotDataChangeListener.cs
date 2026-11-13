namespace PMS.Application.Services;

/// <summary>
/// Told when public lot data changed: a lot was created, edited, deleted, opened or closed, its layout was
/// replaced, or one of its prices changed. Readers that keep a copy of that data (the chat assistant's lot
/// list) drop it, so the next read shows the change at once.
/// </summary>
public interface ILotDataChangeListener
{
    void LotDataChanged(int lotId);
}
