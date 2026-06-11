import { clampBlock, localMinutes } from './day-layout';

describe('day-layout', () => {
  it('positions an event inside the window and clamps the edges', () => {
    expect(clampBlock(8 * 60 + 30, 9 * 60 + 25, { startHour: 7, endHour: 21, pxPerHour: 66 }))
      .toEqual({ top: 99, height: 60.5 });
    expect(clampBlock(6 * 60, 8 * 60, { startHour: 7, endHour: 21, pxPerHour: 60 }))
      .toEqual({ top: 0, height: 60 });
    expect(clampBlock(22 * 60, 23 * 60, { startHour: 7, endHour: 21, pxPerHour: 60 })).toBeNull();
  });

  it('localMinutes converts ISO instants to local minutes-of-day', () => {
    const d = new Date(2026, 5, 10, 14, 30);
    expect(localMinutes(d.toISOString())).toBe(14 * 60 + 30);
    expect(localMinutes('junk')).toBeNull();
  });
});
