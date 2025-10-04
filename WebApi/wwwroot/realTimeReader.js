"use strict";

import {
  attachFocusToList,
  generateNextFocusListOnSelected,
} from "./interactive-select/module.js";
import {
	activateVimScrolling,
} from "./interactive-select/vim-motioned.js";

const listOfOptionsClassName = "navigation-list";
const classForNewSelectedItem = "navigation-list-option-selected";
const idForInput = "navigation-input";
const mapOfOptions = new Map();
mapOfOptions.set(1, { items: [1, 2, 3], actionOnEnter: (target) => alert(target.innerText) })
activateVimScrolling();
attachFocusToList(
	classForNewSelectedItem,
	listOfOptionsClassName,
	idForInput,
  (target) => {
		let listConfiguration = mapOfOptions.get(target.tabIndex);
		generateNextFocusListOnSelected(
			listOfOptionsClassName,
			classForNewSelectedItem,
			'',
			listConfiguration.items,
			idForInput,
			target,
			listConfiguration.actionOnEnter
		)
	}
);

let connection = new signalR.HubConnectionBuilder().withUrl("/signalr").build();
let chapterNumber = 200;
let paragraphNumber = 1;
let initializing = true;
let lastSentFromScroll = undefined;
let novelName = "reverend-insanity";
let userName = "Anton";

function isNewChapterTitleParagraph(paragraphNumberFromBackend) {
  return paragraphNumberFromBackend === 2;
}

connection.on(
  "ReturnNextParagraph",
  function (newChapterNumber, newParagraphNumber, paragraphContent) {
    if (newChapterNumber === chapterNumber && initializing) {
      connection.invoke(
        "GetNextParagraph",
        userName,
        novelName,
        chapterNumber,
        newParagraphNumber
      );
      lastSentFromScroll = {
        chapterNumber,
        paragraphNumber,
      };
    } else {
      initializing = false;
    }

    chapterNumber = newChapterNumber;
    paragraphNumber = newParagraphNumber;
    let paragraphElement = document.createElement("p");
    if (isNewChapterTitleParagraph(newParagraphNumber)) {
      paragraphElement.className = "data-chapter-title";
    }

    paragraphElement.innerText = paragraphContent;
    document.getElementById("novel-paragraphs").appendChild(paragraphElement);
  }
);

connection
  .start()
  .then(function () {
    console.log("Started Connection");
    connection.invoke(
      "GetNextParagraph",
      userName,
      novelName,
      chapterNumber,
      paragraphNumber
    );
  })
  .catch(function (error) {
    console.error(error.toString());
  });

window.addEventListener("scroll", function () {
  if (window.innerHeight + window.scrollY >= document.body.scrollHeight - 100) {
    if (
      lastSentFromScroll &&
      (lastSentFromScroll.chapterNumber !== chapterNumber ||
        lastSentFromScroll.paragraphNumber !== paragraphNumber)
    ) {
      lastSentFromScroll = {
        chapterNumber,
        paragraphNumber,
      };
      connection.invoke(
        "GetNextParagraph",
        userName,
				novelName,
        chapterNumber,
        paragraphNumber
      );
    }
  }
});
