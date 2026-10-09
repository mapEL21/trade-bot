import { Code2, Plus } from 'lucide-react';
import type { StrategyDefinition } from './api';

export function Strategies({
  items,
  demo,
  onCreate,
}: {
  items: StrategyDefinition[];
  demo: boolean;
  onCreate: () => void;
}) {
  return (
    <>
      <div className="inline-note strategy-explanation">
        <Code2 size={22} />
        <span>
          <strong>Стратегия — код алгоритма. Бот — его настроенный экземпляр.</strong>
          <br />
          Здесь будут версии наших алгоритмов. При создании экземпляра задаются биржа, инструмент и
          лимиты. Импорт JSON переносит только настройки; загрузка кода пока не поддерживается.
        </span>
      </div>
      <div className="connection-grid">
        {items.map((item) => (
        <article className="panel exchange-card strategy-card" key={`${item.id}-${item.version}`}>
            <div className="bot-icon">
              <Code2 size={25} />
            </div>
            <h2>{item.name}</h2>
            <span className="neutral-badge">
              {item.canRun ? 'Реализована' : 'В разработке'} · v{item.version}
            </span>
            <p>{item.description}</p>
            <p>Биржи в настройках: {item.exchanges.join(', ')}. Подключения ещё отсутствуют.</p>
            <button className="button primary" disabled={!item.canCreate} onClick={onCreate}>
              <Plus size={16} />
              Создать экземпляр
            </button>
            <p className="field-hint">
              {demo
                ? 'Настройки останутся в демопространстве браузера.'
                : 'Сохраним черновик на сервере. Запуск станет доступен после реализации алгоритма.'}
            </p>
          </article>
        ))}
      </div>
    </>
  );
}
