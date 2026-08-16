import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  {
    path: 'dashboard',
    title: 'Argus - Dashboard',
    loadComponent: () => import('./pages/dashboard.component').then((m) => m.DashboardComponent),
  },
  {
    path: 'explorer',
    title: 'Argus - Explorer',
    loadComponent: () => import('./pages/explorer.component').then((m) => m.ExplorerComponent),
  },
  {
    path: 'view/:handle',
    title: 'Argus - Live',
    loadComponent: () => import('./pages/viewer.component').then((m) => m.ViewerComponent),
  },
  { path: '**', redirectTo: 'dashboard' },
];
