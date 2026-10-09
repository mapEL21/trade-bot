import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { createDemo, exportConfig, STORAGE_KEY } from '../../src/model';

test('overview, trade periods and bot navigation work without console errors', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto('/?demo=1');
  await expect(page.getByRole('heading', { name: 'Всё под контролем' })).toBeVisible();
  await page.screenshot({
    path: 'artifacts/overview-desktop.png',
    fullPage: true,
  });
  await page.getByRole('button', { name: 'Статистика' }).click();
  await expect(page.getByRole('heading', { name: 'Импульс · ARB' })).toBeVisible();
  await page.getByRole('button', { name: 'Запустить демо', exact: true }).click();
  await page.locator('.detail-tabs').getByRole('button', { name: 'Сделки', exact: true }).click();
  const week = await page.locator('tbody tr').count();
  await page.getByRole('button', { name: '24 часа', exact: true }).click();
  expect(await page.locator('tbody tr').count()).toBeLessThan(week);
  await page.getByRole('button', { name: 'Настройки', exact: true }).click();
  await page.getByLabel('Название бота').fill('Мой пример');
  await page.getByRole('button', { name: 'Сохранить изменения' }).click();
  await expect(page.getByRole('heading', { name: 'Мой пример' })).toBeVisible();
  await page.getByRole('button', { name: 'Журнал', exact: true }).click();
  await expect(page.getByText(/Изменён только статус интерфейса/)).toBeVisible();
  expect(errors).toEqual([]);
});

for (const width of [375, 768, 1440]) {
  test(`responsive overview and accessible forms at ${width}px`, async ({ page }) => {
    const failures: string[] = [];
    page.on('response', (response) => {
      if (response.status() >= 400) failures.push(`${response.status()} ${response.url()}`);
    });
    page.on('pageerror', (error) => failures.push(error.message));
    await page.setViewportSize({ width, height: 1000 });
    await page.goto('/?demo=1');
    await page.evaluate(() => document.fonts.ready);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
    await page.screenshot({ path: `artifacts/overview-${width}.png`, fullPage: true });
    const audit = () =>
      new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
    expect(
      (await audit()).violations.map((v) => ({
        id: v.id,
        nodes: v.nodes.map((n) => ({ target: n.target, reason: n.failureSummary })),
      })),
    ).toEqual([]);
    const create = page.getByRole('button', { name: 'Создать бота', exact: true }).first();
    await create.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByLabel('Название бота')).toBeFocused();
    expect(
      (await audit()).violations.map((v) => ({
        id: v.id,
        nodes: v.nodes.map((n) => ({ target: n.target, reason: n.failureSummary })),
      })),
    ).toEqual([]);
    await page.keyboard.press('Escape');
    await expect(create).toBeFocused();
    expect(failures).toEqual([]);
  });
}

test('create, persist, pause, export, import and delete a bot', async ({ page }) => {
  await page.goto('/?demo=1');
  await page.getByRole('button', { name: 'Создать бота', exact: true }).first().click();
  await page.getByLabel('Название бота').fill('Тестовый бот');
  await page.getByRole('dialog').getByRole('button', { name: 'Создать бота', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Тестовый бот' })).toBeVisible();
  await expect(page.getByText('Здесь появится кривая результата')).toBeVisible();
  await page.getByRole('button', { name: 'Запустить демо', exact: true }).click();
  await expect(page.getByText('Демо запущено', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Пауза', exact: true }).click();
  await page.reload();
  await page.getByRole('button', { name: 'Мои боты', exact: false }).first().click();
  await page.getByRole('button', { name: 'Тестовый бот Импульс v0.1.0' }).click();
  await expect(page.getByText('На паузе', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Настройки', exact: true }).click();
  const downloadPromise = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Экспорт', exact: true }).click();
  const file = await downloadPromise;
  expect(file.suggestedFilename()).toBe('arbusdt-bot.json');
  await page.getByRole('button', { name: 'Все боты', exact: true }).click();
  await page.getByRole('button', { name: 'Импорт бота', exact: true }).click();
  await page
    .getByLabel('Или вставьте содержимое JSON')
    .fill(exportConfig({ ...createDemo().bots[0], name: 'Импортированный бот' }));
  await page.getByRole('button', { name: 'Импортировать', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Импортированный бот' })).toBeVisible();
  await expect(page.getByText('Черновик', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Настройки', exact: true }).click();
  await page.getByRole('button', { name: 'Удалить', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Удалить', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Импортированный бот Импульс v0.1.0' }),
  ).toHaveCount(0);
});

test('invalid imports and history-changing edits are rejected', async ({ page }) => {
  await page.goto('/?demo=1');
  await page.getByRole('button', { name: 'Импорт бота', exact: true }).click();
  await page.getByLabel('Или вставьте содержимое JSON').fill('{"schemaVersion":5}');
  await page.getByRole('button', { name: 'Импортировать', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('версии 1');
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.getByRole('button', { name: 'Статистика', exact: true }).click();
  await page.getByRole('button', { name: 'Настройки', exact: true }).click();
  await page.getByLabel('USDT-фьючерс').fill('OPUSDT');
  await page.getByRole('button', { name: 'Сохранить изменения' }).click();
  await expect(page.getByRole('alert')).toContainText('Создайте копию');
});

test('mobile navigation and modal fit the viewport', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/?demo=1');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({
    path: 'artifacts/overview-mobile.png',
    fullPage: true,
  });
  await page.getByRole('button', { name: 'Открыть меню' }).click();
  await page.getByRole('button', { name: 'Мои боты', exact: false }).first().click();
  await page.getByRole('button', { name: 'Создать бота', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.screenshot({
    path: 'artifacts/create-mobile.png',
    fullPage: true,
  });
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('corrupt storage is reported and requires explicit reset', async ({ page }) => {
  await page.addInitScript((key) => localStorage.setItem(key, '{broken'), STORAGE_KEY);
  await page.goto('/?demo=1');
  await expect(page.getByRole('alert')).toContainText('повреждены');
  expect(await page.evaluate((key) => localStorage.getItem(key), STORAGE_KEY)).toBe('{broken');
  await page.getByRole('button', { name: 'Подключения', exact: true }).click();
  await page.getByRole('button', { name: 'Сбросить демопространство' }).click();
  await page.getByRole('button', { name: 'Сбросить данные', exact: true }).click();
  await expect(page.getByRole('alert')).toHaveCount(0);
  expect(
    await page.evaluate((key) => JSON.parse(localStorage.getItem(key)!).schemaVersion, STORAGE_KEY),
  ).toBe(1);
});
