function attachFocusToList(
  classForSelectedItem,
  classForList,
  idForInput,
  actionOnEnter
) {
  const list = document.getElementsByClassName(classForList)[0];
  const invisibleListStyle = "opacity: 0; pointer-event: none;";
  const visibleListStyle = "opacity: 1; pointer-events: all;"
  const pressedFRegex = new RegExp("F[0-9]{1,2}");

  const classForTrackingSelected = `${Math.random()} counter-`;

  const mapOfEvents = new Map();
  mapOfEvents.set("j", 1);
  mapOfEvents.set("k", -1);
  mapOfEvents.set("ArrowDown", -1);
  mapOfEvents.set("ArrowUp", 1);

  let elementsInSelectCount = 0;
  let selected = 0;

  function changeSelectedItem(counterStep) {
    if (selected + counterStep > elementsInSelectCount) {
      selected = 0;
    } else if (selected + counterStep < 0) {
      selected = elementsInSelectCount;
    } else {
      selected += counterStep;
    }

    const nextActiveElement = document
      .getElementsByClassName(`${classForTrackingSelected}${selected}`)[0];
    nextActiveElement.focus();
  }

  function addFocusEventListenersForElementsInsideSelector(element) {
    element.addEventListener("focus", (event) => {
      list.style = visibleListStyle;
      if (!event.target.className.includes(classForSelectedItem))
        event.target.className += ` ${classForSelectedItem}`;
    });

    element.addEventListener("focusout", (event) => {
      if (!event.relatedTarget) {
        list.style = invisibleListStyle;
      }

      event.target.className = event.target.className.replace(
        ` ${classForSelectedItem}`,
        ""
      );
    });
  }

  function addMotionsEventListenersForOptionElement(element) {
    element.className += ` ${classForTrackingSelected}${elementsInSelectCount++}`;
    element.addEventListener("keydown", (event) => {
      if (event.key === 'Tab' || pressedFRegex.exec(event.key)){
        return
      }

      event.preventDefault();
      if (event.key === '?') {
        document.getElementById(idForInput).focus();
      }

      if (event.key === "Escape") {
        document.activeElement.blur();
      }

      if (event.key === "Enter") {
        actionOnEnter(event.target);
      }

      let counterStep = mapOfEvents.get(event.key);
      if (counterStep) {
        changeSelectedItem(counterStep);
      }
    });
  }

  if (!list) {
    console.error("List to attach keyboard binding wasnt found");
  }

  list.childNodes.forEach((element) => {
    if (!element.tagName)
      return;

    addFocusEventListenersForElementsInsideSelector(element);
    if (element.tagName === "INPUT") return;

    addMotionsEventListenersForOptionElement(element);
    if (elementsInSelectCount === 1 && document.activeElement.tagName === "BODY") {
      element.focus();
    }
  });
  elementsInSelectCount--;
  list.style = "opacity: 0; pointer-event: none;"
  document.getElementById(idForInput)
    .addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      document.activeElement.blur();
    }
  });
}

function generateNextFocusListOnSelected(
  classForNewList,
  classForNewSelectedItem,
  classForNewOptions,
  listOfItems,
  idForInput,
  target,
  actionOnEnter,
  placeholderForInput = 'Search...'
) {
  const parentList = target.parentElement;
  const inputElement = document.createElement("input");
  inputElement.type = "search";
  inputElement.id = idForInput;
  inputElement.placeholder = placeholderForInput;
  const newElement = document.createElement("ul");
  newElement.appendChild(inputElement);
  newElement.className = classForNewList;
  for (let i = 0; i < listOfItems.length; i++) {
    const newOption = document.createElement("li");
    newOption.innerText = listOfItems[i];
    newOption.tabIndex = 0;
    newOption.className = classForNewOptions;
    newElement.appendChild(newOption);
  }

  parentList.parentElement.appendChild(newElement);
  parentList.remove();

  attachFocusToList(classForNewSelectedItem, classForNewList, idForInput, actionOnEnter);
}


export { attachFocusToList, generateNextFocusListOnSelected}