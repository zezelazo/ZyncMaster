import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./features/auth/login.component').then(m => m.LoginComponent) },
  { path: 'auth/callback', loadComponent: () => import('./features/auth/auth-callback.component').then(m => m.AuthCallbackComponent) },
  { path: 'calendar', canActivate: [authGuard], loadComponent: () => import('./features/calendar/calendar-page.component').then(m => m.CalendarPageComponent) },
  { path: '', pathMatch: 'full', redirectTo: 'calendar' },
  { path: '**', redirectTo: 'calendar' },
];
