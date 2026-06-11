import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthService } from './auth.service';
import { TokenStore } from './token-store';

describe('AuthService', () => {
  let service: AuthService;
  let http: HttpTestingController;
  let tokens: TokenStore;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
    tokens = TestBed.inject(TokenStore);
    localStorage.clear();
    sessionStorage.clear();
  });

  afterEach(() => http.verify());

  it('requestMagicLink posts email + web mode + a stored nonce', async () => {
    const done = service.requestMagicLink('z@x.com');
    const req = http.expectOne('/zync/identity/magic-link');
    expect(req.request.method).toBe('POST');
    expect(req.request.body.email).toBe('z@x.com');
    expect(req.request.body.web).toBe(true);
    expect(req.request.body.nonce).toBe(sessionStorage.getItem('zw.nonce'));
    req.flush({}, { status: 202, statusText: 'Accepted' });
    await done;
  });

  it('redeemHandle verifies the nonce and stores the session', async () => {
    sessionStorage.setItem('zw.nonce', 'n-1');
    const done = service.redeemHandle('h-1', 'n-1');
    const req = http.expectOne('/zync/identity/handle/redeem');
    expect(req.request.body).toEqual({ handle: 'h-1' });
    req.flush({ accessToken: 'acc-9', refreshToken: 'ref-9' });
    expect(await done).toBe(true);
    expect(tokens.access()).toBe('acc-9');
    expect(tokens.refresh).toBe('ref-9');
  });

  it('redeemHandle rejects a nonce mismatch without calling the server', async () => {
    sessionStorage.setItem('zw.nonce', 'n-1');
    expect(await service.redeemHandle('h-1', 'WRONG')).toBe(false);
    http.expectNone('/zync/identity/handle/redeem');
  });

  it('refresh rotates the pair and clears the session when the server rejects it', async () => {
    tokens.setSession('old-acc', 'old-ref');
    const ok = service.refresh();
    const req = http.expectOne('/zync/identity/refresh');
    expect(req.request.body).toEqual({ refreshToken: 'old-ref' });
    req.flush({ accessToken: 'new-acc', newRefreshToken: 'new-ref' });
    expect(await ok).toBe(true);
    expect(tokens.access()).toBe('new-acc');

    const bad = service.refresh();
    http.expectOne('/zync/identity/refresh').flush('nope', { status: 401, statusText: 'Unauthorized' });
    expect(await bad).toBe(false);
    expect(tokens.signedIn).toBe(false);
  });
});
