import { TokenStore } from './token-store';

describe('TokenStore', () => {
  beforeEach(() => localStorage.clear());

  it('starts signed out', () => {
    const store = new TokenStore();
    expect(store.signedIn).toBe(false);
    expect(store.access()).toBeNull();
  });

  it('setSession keeps the access token in memory and the refresh token in localStorage', () => {
    const store = new TokenStore();
    store.setSession('acc-1', 'ref-1');
    expect(store.access()).toBe('acc-1');
    expect(store.refresh).toBe('ref-1');
    expect(localStorage.getItem('zw.refresh')).toBe('ref-1');
  });

  it('clear wipes both tokens', () => {
    const store = new TokenStore();
    store.setSession('acc-1', 'ref-1');
    store.clear();
    expect(store.signedIn).toBe(false);
    expect(localStorage.getItem('zw.refresh')).toBeNull();
  });
});
