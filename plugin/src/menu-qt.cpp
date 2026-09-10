/*
Backcast Projector — main menu bar integration.
Copyright (C) 2026 Marsic1

This program is free software; you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation; either version 2 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License along
with this program. If not, see <https://www.gnu.org/licenses/>
*/

/* The obs-frontend-api only offers Tools-menu registration; a top-level
 * menu-bar button needs Qt. This tiny C++ TU does the Qt part — the rest
 * of the plugin stays plain C. */

#include <QWidget>
#include <QMainWindow>
#include <QMenuBar>
#include <QAction>

extern "C" void bcp_add_menubar_button(void *main_window, void (*callback)(void *), void *private_data)
{
	if (!main_window || !callback)
		return;

	auto *win = static_cast<QMainWindow *>(static_cast<QWidget *>(main_window));
	QMenuBar *bar = win->menuBar();
	if (!bar)
		return;

	/* top-level entry: clickable directly in the menu bar, no submenu */
	QAction *act = bar->addAction(QStringLiteral("BackCast Window"));
	QObject::connect(act, &QAction::triggered, [callback, private_data]() {
		callback(private_data);
	});
}
