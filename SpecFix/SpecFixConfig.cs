using CounterStrikeSharp.API.Core;

namespace SpecFix;

public class SpecFixConfig : BasePluginConfig
{
    // Главный выключатель всего плагина.
    public bool Enabled { get; set; } = true;

    // Подробные логи в консоль сервера.
    public bool DebugLog { get; set; } = false;

    // Лечение "призрака" при смерти и в спектаторах.
    public bool FixGhostOnDeath { get; set; } = true;

    // Ограничение частоты смены команды.
    public bool RateLimitTeamSwitch { get; set; } = true;

    // Скользящее окно (в секундах), в пределах которого считаются смены.
    public float TeamSwitchWindowSeconds { get; set; } = 5.0f;

    // Сколько смен команды допустимо в окне, прежде чем сработает блок.
    public int MaxTeamSwitchesInWindow { get; set; } = 3;

    // Длительность кулдауна (в секундах) после срабатывания блока.
    public float TeamSwitchCooldownSeconds { get; set; } = 10.0f;
}
