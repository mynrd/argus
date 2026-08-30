import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  {
    path: 'dashboard',
    title: 'Argus - Dashboard',
    loadComponent: () => import('./pages/dashboard.component').then((m) => m.DashboardComponent),
  },
  {
    path: 'ports',
    title: 'Argus - Ports',
    loadComponent: () => import('./pages/ports.component').then((m) => m.PortsComponent),
  },
  {
    path: 'explorer',
    title: 'Argus - Explorer',
    loadComponent: () => import('./pages/explorer.component').then((m) => m.ExplorerComponent),
  },
  {
    path: 'settings',
    title: 'Argus - Settings',
    loadComponent: () => import('./pages/settings.component').then((m) => m.SettingsComponent),
  },
  {
    path: 'view/:handle',
    title: 'Argus - Live',
    loadComponent: () => import('./pages/viewer.component').then((m) => m.ViewerComponent),
  },
  { path: '**', redirectTo: 'dashboard' },
];
