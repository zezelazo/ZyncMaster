import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRouteSnapshot, provideRouter, RouterStateSnapshot, UrlTree } from '@angular/router';
import { authGuard } from './auth.guard';
import { TokenStore } from './token-store';

describe('authGuard', () => {
  const dummyRoute = {} as ActivatedRouteSnapshot;
  const dummyState = {} as RouterStateSnapshot;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    localStorage.clear();
  });

  const run = () =>
    TestBed.runInInjectionContext(() => authGuard(dummyRoute, dummyState)) as Promise<boolean | UrlTree>;

  it('passes when already signed in', async () => {
    TestBed.inject(TokenStore).setSession('acc-1', 'ref-1');
    expect(await run()).toBe(true);
  });

  it('recovers the session with one silent refresh after a page reload', async () => {
    const tokens = TestBed.inject(TokenStore);
    tokens.setSession('acc-1', 'ref-1');
    tokens.clear();
    localStorage.setItem('zw.refresh', 'ref-1'); // reload: memory gone, refresh persisted
    const result = run();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/zync/identity/refresh')
      .flush({ accessToken: 'acc-2', newRefreshToken: 'ref-2' });
    expect(await result).toBe(true);
    expect(tokens.access()).toBe('acc-2');
    http.verify();
  });

  it('bounces to /login when there is no session to refresh', async () => {
    const result = await run();
    expect(result instanceof UrlTree).toBe(true);
    expect((result as UrlTree).toString()).toBe('/login');
  });
});
