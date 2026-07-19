/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared;

namespace Source2Surf.Timer.Managers;

/// <summary>
///     Base for proxies that forward a Timer.Shared contract to an externally registered
///     ModSharp module when one is present, and to a built-in fallback otherwise. Owns the
///     refresh/swap plumbing and the lazy, thread-safe fallback initialization; derived
///     classes contribute only the Identity/fallback hooks and the forwarding members
///     (which should dispatch through <see cref="Current" />).
/// </summary>
internal abstract class ExternalModuleProxy<TInterface> : IManager
    where TInterface : class
{
    private readonly ISharedSystem _shared;
    private readonly TInterface    _fallback;
    private readonly ILogger       _logger;
    private readonly object        _fallbackLock = new ();

    private TInterface _current;
    private bool       _fallbackInitialized;

    protected ExternalModuleProxy(ISharedSystem shared, TInterface fallback, ILogger logger)
    {
        _shared   = shared;
        _fallback = fallback;
        _current  = fallback;
        _logger   = logger;
    }

    protected TInterface Current => Volatile.Read(ref _current);

    /// <summary>ModSharp Identity string the external module registers under.</summary>
    protected abstract string Identity { get; }

    /// <summary>Contract name used in log messages (e.g. "ICommandManager").</summary>
    protected abstract string ContractName { get; }

    protected abstract bool InitFallback();

    protected abstract void ShutdownFallback();

    public bool Init()
    {
        try
        {
            RefreshManager();

            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to initialize {contract} proxy.", ContractName);

            return false;
        }
    }

    public void Shutdown()
    {
        if (_fallbackInitialized)
        {
            ShutdownFallback();
            _fallbackInitialized = false;
        }
    }

    public void RefreshManager()
    {
        // .Instance: log the provider implementation, not the ISharpModuleInterface wrapper.
        var external = _shared.GetSharpModuleManager()
                              .GetOptionalSharpModuleInterface<TInterface>(Identity)
                              ?.Instance;

        if (external is not null && !ReferenceEquals(external, this))
        {
            Use(external, external.GetType().FullName);

            return;
        }

        UseFallback();
    }

    public void Use(TInterface manager, string? providerName = null)
    {
        if (ReferenceEquals(manager, _fallback))
        {
            EnsureFallbackInitialized();
        }

        if (ReferenceEquals(Current, manager))
        {
            return;
        }

        Volatile.Write(ref _current, manager);

        if (!ReferenceEquals(manager, _fallback))
        {
            if (!string.IsNullOrWhiteSpace(providerName))
            {
                _logger.LogInformation("Using external {contract} from {provider}.", ContractName, providerName);
            }
            else
            {
                _logger.LogInformation("Using custom {contract} instance.", ContractName);
            }
        }
        else
        {
            _logger.LogInformation("Using built-in {contract}: {type}",
                                   ContractName,
                                   _fallback.GetType().FullName);
        }
    }

    public void UseFallback()
        => Use(_fallback);

    private void EnsureFallbackInitialized()
    {
        if (Volatile.Read(ref _fallbackInitialized))
        {
            return;
        }

        lock (_fallbackLock)
        {
            if (_fallbackInitialized)
            {
                return;
            }

            if (!InitFallback())
            {
                throw new InvalidOperationException($"Failed to initialize built-in {ContractName} fallback.");
            }

            Volatile.Write(ref _fallbackInitialized, true);
        }
    }
}
