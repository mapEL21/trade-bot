import { useEffect, useState } from 'react';
import { api, type BotTemplate } from './api';
import type { BotConfig } from './model';
import { builtInTemplates, defaultFees } from './strategySettings';

// Templates of bot settings in the bot form: apply one, save the current settings under a name, delete your own.
// A template holds the balance, loss limit, leverage, order delay and strategy settings, not exchanges or pair.
export function TemplatePicker({
  form,
  setForm,
}: {
  form: BotConfig;
  setForm: (update: (f: BotConfig) => BotConfig) => void;
}) {
  const [saved, setSaved] = useState<BotTemplate[]>([]);
  const [chosen, setChosen] = useState(builtInTemplates[0].id);
  const [name, setName] = useState('');
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    api
      .templates(controller.signal)
      .then((list) => {
        if (!controller.signal.aborted) setSaved(list);
      })
      .catch((e) => {
        if (!controller.signal.aborted) setMessage((e as Error).message);
      });
    return () => controller.abort();
  }, []);
  const all = [...builtInTemplates, ...saved];
  const template = all.find((t) => t.id === chosen) ?? builtInTemplates[0];

  function apply() {
    setForm((f) => {
      const p = f.parameters!;
      const fees = defaultFees[f.exchange];
      const settings = {
        ...template.settings,
        // Saved by an older version: the reversal threshold was the entry impulse.
        reversalBps: template.settings.reversalBps ?? template.settings.impulseBps,
        leader: 'source' as const,
        // Built-in templates keep the fees of the bot's exchange; saved ones carry the fees they were saved with.
        ...(template.builtIn ? { feeBps: fees.taker, makerFeeBps: fees.maker } : {}),
      };
      return {
        ...f,
        budget: template.budget,
        lossLimit: template.lossLimit,
        parameters: {
          ...p,
          leverage: template.leverage,
          delayMs: template.delayMs,
          counter: template.counter,
          settings,
        },
      };
    });
    setMessage(
      `Применён шаблон «${template.name}». Сохраните бота, чтобы настройки вступили в силу.`,
    );
  }

  async function save() {
    const p = form.parameters!;
    if (!name.trim()) {
      setMessage('Укажите название шаблона.');
      return;
    }
    setBusy(true);
    try {
      const result = await api.saveTemplate({
        name: name.trim(),
        strategy: 'leader-lag',
        budget: form.budget,
        lossLimit: form.lossLimit,
        leverage: p.leverage,
        delayMs: p.delayMs,
        counter: p.counter,
        settings: p.settings,
      });
      setSaved((list) =>
        [...list.filter((t) => t.id !== result.id), result].sort((a, b) =>
          a.name.localeCompare(b.name),
        ),
      );
      setChosen(result.id);
      setName('');
      setMessage(`Шаблон «${result.name}» сохранён.`);
    } catch (e) {
      setMessage((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    setBusy(true);
    try {
      await api.deleteTemplate(template.id);
      setSaved((list) => list.filter((t) => t.id !== template.id));
      setChosen(builtInTemplates[0].id);
      setMessage(`Шаблон «${template.name}» удалён.`);
    } catch (e) {
      setMessage((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <fieldset className="template-picker">
      <legend>Шаблон настроек</legend>
      <div className="template-row">
        <label className="field">
          <span>Шаблон</span>
          <select value={template.id} onChange={(e) => setChosen(e.target.value)}>
            <optgroup label="Встроенные">
              {builtInTemplates.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </optgroup>
            {saved.length > 0 && (
              <optgroup label="Мои шаблоны">
                {saved.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.name}
                  </option>
                ))}
              </optgroup>
            )}
          </select>
        </label>
        <button type="button" className="button secondary" disabled={busy} onClick={apply}>
          Применить
        </button>
        {!template.builtIn && (
          <button
            type="button"
            className="text-button"
            disabled={busy}
            onClick={() => void remove()}
          >
            Удалить шаблон
          </button>
        )}
      </div>
      <p className="field-hint">
        {template.description ??
          `Баланс ${template.budget} USDT, плечо ${template.leverage}x, лимит убытка ${template.lossLimit} USDT.`}{' '}
        Биржи и пара не меняются.
      </p>
      <div className="template-row">
        <label className="field">
          <span>Сохранить текущие настройки как шаблон</span>
          <input
            value={name}
            maxLength={40}
            placeholder="Название шаблона"
            onChange={(e) => setName(e.target.value)}
          />
        </label>
        <button
          type="button"
          className="button secondary"
          disabled={busy}
          onClick={() => void save()}
        >
          Сохранить шаблон
        </button>
      </div>
      {message && (
        <p className="field-hint" role="status">
          {message}
        </p>
      )}
    </fieldset>
  );
}
