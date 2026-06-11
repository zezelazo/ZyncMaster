// Node >= 22 ships experimental `localStorage`/`sessionStorage` globals that are
// non-functional without `--localstorage-file` and SHADOW jsdom's implementations in
// the Vitest node+jsdom environment. Replace them with a real in-memory Storage so
// specs and the code under test share working web storage.
class MemoryStorage implements Storage {
  private readonly map = new Map<string, string>();
  get length(): number { return this.map.size; }
  clear(): void { this.map.clear(); }
  getItem(key: string): string | null { return this.map.get(key) ?? null; }
  key(index: number): string | null { return [...this.map.keys()][index] ?? null; }
  removeItem(key: string): void { this.map.delete(key); }
  setItem(key: string, value: string): void { this.map.set(key, String(value)); }
}

for (const key of ['localStorage', 'sessionStorage'] as const) {
  Object.defineProperty(globalThis, key, {
    configurable: true,
    value: new MemoryStorage(),
  });
}
