// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Linq;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A service for dealing with stream information.
/// </summary>
/// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
public sealed class TaskService(ITaskManager taskManager, ILogger<TaskService> logger)
{
    private static Type? FindType(string assembly, string fullName)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a =>
                !a.IsDynamic &&
                (a.FullName?.StartsWith($"{assembly},", StringComparison.InvariantCulture) ?? false))
            .Select(a => a.GetType(fullName, throwOnError: false, ignoreCase: false))
            .FirstOrDefault(t => t is not null);
    }

    /// <summary>
    /// Executes a task from the given assembly and name.
    /// </summary>
    /// <param name="assembly">The name of the assembly to search in for the type.</param>
    /// <param name="fullName">The full name of the task type.</param>
    public void CancelIfRunningAndQueue(string assembly, string fullName)
    {
        try
        {
            Type? refreshType = FindType(assembly, fullName);
            if (refreshType is null)
            {
                logger.LogWarning(
                    "Scheduled task {TaskType} from assembly {AssemblyName} was not found; refresh was not queued.",
                    fullName,
                    assembly);
                return;
            }

            // As some Jellyfin task types are not publicly visible, invoke the generic API by reflection.
            var queueMethod = typeof(ITaskManager)
                .GetMethod(nameof(ITaskManager.CancelIfRunningAndQueue), 1, []);
            if (queueMethod is null)
            {
                logger.LogWarning("The Jellyfin task queue API was not found; refresh was not queued.");
                return;
            }

            queueMethod.MakeGenericMethod(refreshType).Invoke(taskManager, []);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not queue scheduled task {TaskType}; the configuration change was still saved.",
                fullName);
        }
    }
}
