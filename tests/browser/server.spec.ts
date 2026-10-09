import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { createDemo, exportConfig, STORAGE_KEY } from '../../src/model';

test('catalog creates a server instance that survives a new browser, edits, exports and deletes', async ({
  page,
  browser,
  request,
}) => {
  const name = `Серверный ${Date.now()}`;
  let id: string | undefined;
  try {
    await page.goto('/');
    await expect(page.getByText('Данные загружены с сервера')).toBeVisible();
    await page.getByRole('button', { name: 'Стратегии', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Импульс', exact: true })).toBeVisible();
    await expect(page.getByText('В разработке · v0.1.0')).toBeVisible();
    expect(
      (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze()).violations,
    ).toEqual([]);
    await page.getByRole('button', { name: 'Создать экземпляр' }).click();
    await page.getByLabel('Название бота').fill(name);
    const responsePromise = page.waitForResponse(
      (response) =>
        response.url().endsWith('/api/v1/bots') && response.request().method() === 'POST',
    );
    await page
      .getByRole('dialog')
      .getByRole('button', { name: 'Создать бота', exact: true })
      .click();
    const response = await responsePromise;
    expect(response.status()).toBe(201);
    id = (await response.json()).id;
    await expect(page.getByRole('heading', { name })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Алгоритм в разработке' })).toBeDisabled();
    await expect(page.getByText('Здесь появится кривая результата')).toBeVisible();
    expect(await page.evaluate((key) => localStorage.getItem(key), STORAGE_KEY)).toBeNull();

    const secondContext = await browser.newContext();
    const second = await secondContext.newPage();
    await second.goto('http://127.0.0.1:5175/');
    await expect(second.getByText('Данные загружены с сервера')).toBeVisible();
    await second.getByRole('button', { name: 'Мои боты', exact: false }).first().click();
    await second.getByRole('button', { name: `${name} Импульс v0.1.0` }).click();
    await expect(second.getByRole('heading', { name })).toBeVisible();
    await second.getByRole('button', { name: 'Настройки', exact: true }).click();
    await second.getByLabel('Название бота').fill(`${name} v2`);
    await second.getByRole('button', { name: 'Сохранить изменения' }).click();
    await expect(second.getByRole('heading', { name: `${name} v2` })).toBeVisible();
    await secondContext.close();

    await page.getByRole('button', { name: 'Настройки', exact: true }).click();
    await page.getByLabel('Название бота').fill(`${name} stale`);
    await page.getByRole('button', { name: 'Сохранить изменения' }).click();
    await expect(page.getByRole('alert').first()).toContainText('другой вкладке');
    await page.getByRole('button', { name: 'Обновить данные', exact: true }).last().click();
    await expect(page.getByRole('heading', { name: `${name} v2` })).toBeVisible();
    const downloadPromise = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Экспорт', exact: true }).click();
    expect((await downloadPromise).suggestedFilename()).toBe('arbusdt-bot.json');
    await page.getByRole('button', { name: 'Удалить', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Удалить', exact: true }).click();
    await expect(page.getByRole('button', { name: `${name} v2 Импульс v0.1.0` })).toHaveCount(0);
  } finally {
    if (id) {
      const stored = await request.get(`/api/v1/bots/${id}`);
      if (stored.ok())
        await request.delete(`/api/v1/bots/${id}`, {
          headers: {
            'X-TradeBot-Client': 'web',
            'If-Match': `"${(await stored.json()).revision}"`,
          },
        });
    }
  }
});

test('server failure is explicit and does not fall back to browser demo or claim success', async ({
  page,
}) => {
  await page.addInitScript(({ key, data }) => localStorage.setItem(key, data), {
    key: STORAGE_KEY,
    data: JSON.stringify(createDemo()),
  });
  await page.route('**/api/v1/**', (route) => route.abort());
  await page.goto('/');
  await expect(page.getByRole('alert')).toContainText('Сервер недоступен');
  await expect(
    page.getByRole('button', { name: 'Создать бота', exact: true }).first(),
  ).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Импульс · ARB Импульс v0.1.0' })).toHaveCount(0);
  await page.unroute('**/api/v1/**');
  await page.getByRole('button', { name: 'Обновить данные', exact: true }).last().click();
  await expect(page.getByText('Данные загружены с сервера')).toBeVisible();
  await page.route('**/api/v1/bots', (route) =>
    route.fulfill({
      status: 503,
      contentType: 'application/problem+json',
      body: JSON.stringify({ title: 'База временно недоступна' }),
    }),
  );
  await page.getByRole('button', { name: 'Импорт бота', exact: true }).click();
  await page
    .getByLabel('Или вставьте содержимое JSON')
    .fill(exportConfig({ ...createDemo().bots[0], name: 'Не должен сохраниться' }));
  await page.getByRole('button', { name: 'Импортировать', exact: true }).click();
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText(
    'База временно недоступна',
  );
  await expect(page.getByRole('heading', { name: 'Не должен сохраниться' })).toHaveCount(0);
});
